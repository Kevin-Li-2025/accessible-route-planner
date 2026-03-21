using System.Text;
using System.Threading.RateLimiting;
using AccessCity.API.Configuration;
using AccessCity.API.Data;
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
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AccessCity.API.Extensions;

/// <summary>
/// Centralises all DI registrations for the AccessCity API.
/// Each public method maps to a logical slice of the system.
/// </summary>
public static class DependencyInjection
{
    // ───────────────────────────── Messaging ─────────────────────────────

    public static IServiceCollection AddMessaging(this IServiceCollection services, IConfiguration configuration)
    {
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
        var connectionString = PostgresConnectionStringResolver.Resolve(configuration);
        var schema = PostgresConnectionStringResolver.GetPrimarySearchPath(connectionString);

        options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));

        options.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.UseNetTopologySuite();
            npgsql.MapEnum<DatabaseHazardStatus>("hazard_status");
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

        services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = configuration.GetConnectionString("Redis") ?? "localhost:6379";
        });

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

    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        // Singletons (thread-safe caches)
        services.AddSingleton<ISpatialCacheService, SpatialCacheService>();
        services.AddSingleton<IBloomFilterService, BloomFilterService>();

        // Scoped (per-request)
        services.AddScoped<IMapTileService, MapTileService>();
        services.AddScoped<IRouteGraphRepository, RouteGraphRepository>();
        services.AddScoped<IOsmImportService, OsmImportService>();
        services.AddScoped<RiskScoringService>();
        services.AddScoped<PredictiveRiskModel>();
        services.AddScoped<RoutingService>();
        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IRealHazardDataService, RealHazardDataService>();

        // Typed HTTP clients with resilience
        services.AddHttpClient<Services.External.IOsrmClient, Services.External.OsrmClient>()
            .AddStandardResilienceHandler();

        services.AddHttpClient<Services.External.IOpenStreetMapClient, Services.External.OverpassApiClient>()
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30))
            .AddStandardResilienceHandler();

        services.AddHttpClient("Nominatim", c =>
        {
            c.BaseAddress = new Uri("https://nominatim.openstreetmap.org/");
            c.DefaultRequestHeaders.Add("User-Agent", "AccessCity-App/1.0");
            c.Timeout = TimeSpan.FromSeconds(10);
        }).AddStandardResilienceHandler();

        services.AddHttpClient<Services.External.IUkPoliceDataClient, Services.External.UkPoliceDataClient>()
            .AddStandardResilienceHandler();

        services.AddHttpClient<Services.External.ISafeHavenPlacesClient, Services.External.GooglePlacesClient>()
            .AddStandardResilienceHandler();

        services.AddHttpClient<Services.External.ILiveHazardClient, Services.External.OpenWeatherClient>()
            .AddStandardResilienceHandler();

        services.AddHttpClient<Services.External.IEnvironmentalDataClient, Services.External.EnvironmentalDataClient>()
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15))
            .AddStandardResilienceHandler();

        // Background workers
        services.AddHostedService<Services.Background.OsmImportBackgroundService>();

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
        var jwtKey = configuration["Jwt:Key"]
            ?? "AccessCity_Secret_Key_Secure_Long_Enough_For_HS512_2026_Development_Phase_64_Bytes_Long_!!!_STILL_ENFORCING_LENGTH_HE_HE";

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
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

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetSlidingWindowLimiter(
                    partitionKey: context.User.Identity?.Name
                        ?? context.Connection.RemoteIpAddress?.ToString()
                        ?? "unknown",
                    factory: _ => new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = 100,
                        Window = TimeSpan.FromMinutes(1),
                        SegmentsPerWindow = 4,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 10
                    }));
        });

        return services;
    }

    // ───────────────────────────── Observability ─────────────────────────

    public static IServiceCollection AddObservability(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOpenTelemetry()
            .WithTracing(tracing => tracing
                .AddSource("AccessCity.API")
                .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("AccessCity.API"))
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddEntityFrameworkCoreInstrumentation()
                .AddOtlpExporter());

        services.AddHealthChecks()
            .AddDbContextCheck<AppDbContext>("db", tags: new[] { "ready" });

        return services;
    }

    // ───────────────────────────── Web / MVC ─────────────────────────────

    public static IServiceCollection AddWebServices(this IServiceCollection services)
    {
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
                policy.AllowAnyOrigin()
                    .AllowAnyMethod()
                    .AllowAnyHeader();
            });
        });

        return services;
    }
}
