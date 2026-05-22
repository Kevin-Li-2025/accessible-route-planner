using System.Text;
using System.Security.Claims;
using System.Threading.RateLimiting;
using AccessCity.API.Configuration;
using AccessCity.API.Data;
using AccessCity.API.HealthChecks;
using AccessCity.API.Messaging;
using AccessCity.API.Messaging.Kafka;
using AccessCity.API.Models;
using AccessCity.API.Models.Identity;
using AccessCity.API.Services;
using AccessCity.API.Services.Security;
using AccessCity.API.Validators;
using FluentValidation;
using FluentValidation.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AccessCity.API.Extensions;

/// <summary>
/// Centralises all DI registrations for the AccessCity API.
/// Each public method maps to a logical slice of the system.
/// </summary>
public static class DependencyInjection
{
    private const string DevelopmentJwtKey =
        "AccessCity_Secret_Key_Secure_Long_Enough_For_HS512_2026_Development_Phase_64_Bytes_Long_!!!_STILL_ENFORCING_LENGTH_HE_HE";
    private const string DockerComposeDevelopmentJwtKey = "AccessCity_Secret_Key_For_Dev_2026_Placeholder";

    // ───────────────────────────── Messaging ─────────────────────────────

    public static IServiceCollection AddMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<KafkaOptions>(configuration.GetSection(KafkaOptions.SectionName));
        services.AddSingleton<IIntegrationMessageStore, EfIntegrationMessageStore>();

        bool useKafka = configuration.GetValue<bool>("Messaging:UseKafka");

        if (useKafka)
        {
            services.AddSingleton<IMessageBus, KafkaMessageBus>();
        }
        else
        {
            services.AddSingleton<IMessageBus, InMemoryMessageBus>();
        }

        return services;
    }

    // ───────────────────────────── Database ──────────────────────────────

    public static IServiceCollection AddDatabase(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment env)
    {
        services.AddDbContext<AppDbContext>((serviceProvider, options) =>
        {
            var config = serviceProvider.GetRequiredService<IConfiguration>();
            var connectionString = PostgresConnectionStringResolver.Resolve(config);

            if (env.IsDevelopment()
                && string.IsNullOrEmpty(connectionString)
                && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("USE_POSTGRES")))
            {
                options.UseInMemoryDatabase("AccessCityMemoryDb");
                options.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
            }
            else
            {
                ConfigureNpgsql(options, config);
            }
        });

        return services;
    }

    private static void ConfigureNpgsql(DbContextOptionsBuilder options, IConfiguration configuration)
    {
        var postgresOptions = configuration.GetSection(PostgresOptions.SectionName).Get<PostgresOptions>() ?? new PostgresOptions();
        var connectionString = PostgresConnectionStringResolver.Resolve(configuration, postgresOptions);
        var schema = PostgresConnectionStringResolver.GetPrimarySearchPath(connectionString);

        options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));

        options.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.UseNetTopologySuite();
            npgsql.MapEnum<DatabaseHazardStatus>("hazard_status");
            npgsql.CommandTimeout(Math.Max(1, postgresOptions.CommandTimeoutSeconds));
            if (!string.IsNullOrWhiteSpace(schema))
            {
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", schema);
            }
        });
    }

    // ───────────────────────────── Infrastructure ────────────────────────

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PostgresOptions>(configuration.GetSection(PostgresOptions.SectionName));
        services.Configure<OsmImportOptions>(configuration.GetSection(OsmImportOptions.SectionName));
        services.AddSingleton<AccessCityMetrics>();

        var redisConnection = configuration.GetConnectionString("Redis");
        if (!string.IsNullOrWhiteSpace(redisConnection))
        {
            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redisConnection;
            });
        }
        else
        {
            // Docker / local dev without Redis: HybridCache L2 uses in-process IDistributedCache.
            services.AddDistributedMemoryCache();
        }

        services.AddHttpClient();
        services.AddMemoryCache();

#pragma warning disable EXTEXP0018
        services.AddHybridCache(options =>
        {
            options.DefaultEntryOptions = new HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromHours(1)
            };
        });
#pragma warning restore EXTEXP0018

        return services;
    }

    // ───────────────────────────── Application Services ──────────────────

    public static IServiceCollection AddApplicationServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Singletons (thread-safe caches)
        services.AddSingleton<ISpatialCacheService, SpatialCacheService>();
        services.AddSingleton<IBloomFilterService, BloomFilterService>();
        services.AddSingleton<IRouteCoalescingService, RouteCoalescingService>();
        services.AddSingleton<IRouteJobService, RouteJobService>();

        // Scoped (per-request)
        services.AddScoped<IMapTileService, MapTileService>();
        services.AddScoped<IRouteGraphRepository, RouteGraphRepository>();
        services.AddScoped<IOsmImportService, OsmImportService>();
        services.AddScoped<RiskScoringService>();
        services.AddScoped<PredictiveRiskModel>();
        services.AddScoped<RoutingService>();
        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IRealHazardDataService, RealHazardDataService>();

        var osrmTimeout = TimeSpan.FromSeconds(configuration.GetValue("ExternalApis:Osrm:TimeoutSeconds", 8));
        var policeTimeout = TimeSpan.FromSeconds(configuration.GetValue("ExternalApis:UkPolice:TimeoutSeconds", 6));
        var placesTimeout = TimeSpan.FromSeconds(configuration.GetValue("ExternalApis:GooglePlaces:TimeoutSeconds", 6));
        var weatherTimeout = TimeSpan.FromSeconds(configuration.GetValue("ExternalApis:OpenWeather:TimeoutSeconds", 5));
        var environmentalTimeout = TimeSpan.FromSeconds(configuration.GetValue("ExternalApis:Environmental:TimeoutSeconds", 8));

        // Typed HTTP clients with bounded timeouts and resilience.
        services.AddHttpClient<Services.External.IOsrmClient, Services.External.OsrmClient>(client =>
            {
                client.Timeout = osrmTimeout;
            })
            .AddStandardResilienceHandler();

        // Overpass: no Polly retries — retries multiply tail latency (30s × N) and block /hazards merge.
        services.AddHttpClient<Services.External.IOpenStreetMapClient, Services.External.OverpassApiClient>()
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(14));

        // Geocoding implements its own short retry in the controller; avoid Polly × retry tail latency.
        services.AddHttpClient("Nominatim", c =>
        {
            c.BaseAddress = new Uri("https://nominatim.openstreetmap.org/");
            c.DefaultRequestHeaders.Add("User-Agent", "AccessCity-App/1.0");
            c.Timeout = TimeSpan.FromSeconds(12);
        });

        services.AddHttpClient<Services.External.IUkPoliceDataClient, Services.External.UkPoliceDataClient>(client =>
            {
                client.Timeout = policeTimeout;
            })
            .AddStandardResilienceHandler();

        services.AddHttpClient<Services.External.ISafeHavenPlacesClient, Services.External.GooglePlacesClient>(client =>
            {
                client.Timeout = placesTimeout;
            })
            .AddStandardResilienceHandler();

        services.AddHttpClient<Services.External.ILiveHazardClient, Services.External.OpenWeatherClient>(client =>
            {
                client.Timeout = weatherTimeout;
            })
            .AddStandardResilienceHandler();

        services.AddHttpClient<Services.External.IEnvironmentalDataClient, Services.External.EnvironmentalDataClient>()
            .ConfigureHttpClient(c => c.Timeout = environmentalTimeout)
            .AddStandardResilienceHandler();

        // Background workers. In multi-instance deployments these can be disabled on API replicas
        // and enabled on a dedicated worker container consuming the same Kafka group.
        if (configuration.GetValue("Workers:OsmImport:Enabled", true))
        {
            services.AddHostedService<Services.Background.OsmImportBackgroundService>();
        }

        if (configuration.GetValue("Workers:TileWarming:Enabled", true))
        {
            services.AddHostedService<Services.Background.TileWarmingBackgroundService>();
        }

        // Caching services
        services.AddScoped<IRiskTileCacheService, RiskTileCacheService>();
        services.AddScoped<IRouteCacheService, RouteCacheService>();

        // API Versioning
        services.AddApiVersioning(options =>
        {
            options.DefaultApiVersion = new Asp.Versioning.ApiVersion(1, 0);
            options.AssumeDefaultVersionWhenUnspecified = true;
            options.ReportApiVersions = true;
            options.ApiVersionReader = new Asp.Versioning.UrlSegmentApiVersionReader();
        }).AddApiExplorer(options =>
        {
            options.GroupNameFormat = "'v'VVV";
            options.SubstituteApiVersionInUrl = true;
        });

        return services;
    }

    // ───────────────────────────── Security ──────────────────────────────

    public static IServiceCollection AddSecurity(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment env)
    {
        // Identity
        services.AddIdentityCore<AccessCityUser>(options =>
        {
            options.Password.RequireDigit = false;
            options.Password.RequireLowercase = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireNonAlphanumeric = false;
            options.Password.RequiredLength = 6;
            options.User.RequireUniqueEmail = true;
        })
        .AddEntityFrameworkStores<AppDbContext>()
        .AddDefaultTokenProviders();

        services.AddScoped<IPasswordHasher<AccessCityUser>, Argon2PasswordHasher<AccessCityUser>>();

        // JWT
        var jwtKeys = ResolveJwtSigningKeys(configuration, env);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = jwtKeys[0],
                    IssuerSigningKeyResolver = (_, _, _, _) => jwtKeys,
                    ValidateIssuer = true,
                    ValidIssuer = configuration["Jwt:Issuer"] ?? "AccessCity.API",
                    ValidateAudience = true,
                    ValidAudience = configuration["Jwt:Audience"] ?? "AccessCity.App",
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero
                };
            });

        services.AddAuthorization();

        // Rate limiting
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddFixedWindowLimiter("auth", opt =>
            {
                opt.PermitLimit = env.IsDevelopment() ? 100 : 5;
                opt.Window = TimeSpan.FromMinutes(1);
                opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                opt.QueueLimit = 0;
            });

            var globalPermitLimit = configuration.GetValue("RateLimiting:Global:PermitLimit", 100);
            var globalQueueLimit = configuration.GetValue("RateLimiting:Global:QueueLimit", 10);

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetSlidingWindowLimiter(
                    partitionKey: ResolveRateLimitPartitionKey(context),
                    factory: _ => new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = globalPermitLimit,
                        Window = TimeSpan.FromMinutes(1),
                        SegmentsPerWindow = 4,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = globalQueueLimit
                    }));
        });

        return services;
    }

    private static string ResolveRateLimitPartitionKey(HttpContext context)
    {
        var subject = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.User.FindFirstValue("sub")
            ?? context.User.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(subject))
        {
            return "user:" + subject;
        }

        return "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }

    private static IReadOnlyList<SecurityKey> ResolveJwtSigningKeys(IConfiguration configuration, IWebHostEnvironment env)
    {
        var currentKey = configuration["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(currentKey))
        {
            if (!env.IsDevelopment())
            {
                throw new InvalidOperationException("Jwt:Key must be configured outside Development.");
            }

            currentKey = DevelopmentJwtKey;
        }

        if (!env.IsDevelopment() &&
            (string.Equals(currentKey, DevelopmentJwtKey, StringComparison.Ordinal) ||
             string.Equals(currentKey, DockerComposeDevelopmentJwtKey, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("The development JWT signing key cannot be used outside Development.");
        }

        var keys = new List<string> { currentKey };
        var previousKeys = configuration["Jwt:PreviousKeys"];
        if (!string.IsNullOrWhiteSpace(previousKeys))
        {
            keys.AddRange(previousKeys.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return keys
            .Distinct(StringComparer.Ordinal)
            .Select(key => new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)))
            .Cast<SecurityKey>()
            .ToList();
    }

    // ───────────────────────────── Observability ─────────────────────────

    public static IServiceCollection AddObservability(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOpenTelemetry()
            .ConfigureResource(rb => rb.AddService("AccessCity.API"))
            .WithTracing(tracing => tracing
                .AddSource("AccessCity.API")
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddEntityFrameworkCoreInstrumentation()
                .AddOtlpExporter())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(AccessCityMetrics.MeterName)
                .AddMeter("Microsoft.EntityFrameworkCore")
                .AddOtlpExporter());

        services.AddHealthChecks()
            .AddDbContextCheck<AppDbContext>("db", tags: new[] { "ready" })
            .AddCheck<DistributedCacheHealthCheck>("cache", tags: new[] { "ready" })
            .AddCheck<KafkaHealthCheck>("kafka", tags: new[] { "ready" });

        return services;
    }

    // ───────────────────────────── Web / MVC ─────────────────────────────

    public static IServiceCollection AddWebServices(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment env)
    {
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();
        });

        services.AddControllers(options =>
        {
            options.Filters.Add<Filters.OverpassExceptionFilter>();
            options.Filters.Add<Filters.BadRequestExceptionFilter>();
        })
        .AddJsonOptions(options =>
        {
            var factory = new NetTopologySuite.IO.Converters.GeoJsonConverterFactory();
            options.JsonSerializerOptions.Converters.Add(factory);
            options.JsonSerializerOptions.NumberHandling =
                System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals;
        });

        services.AddFluentValidationAutoValidation();
        services.AddValidatorsFromAssemblyContaining<CreateHazardRequestValidator>();

        services.AddOpenApi();
        services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                var allowedOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
                if (env.IsDevelopment() && allowedOrigins.Length == 0)
                {
                    policy.AllowAnyOrigin()
                        .AllowAnyMethod()
                        .AllowAnyHeader();
                    return;
                }

                policy.WithOrigins(allowedOrigins)
                    .AllowAnyMethod()
                    .AllowAnyHeader();
            });
        });

        services.AddSignalR();

        return services;
    }
}
