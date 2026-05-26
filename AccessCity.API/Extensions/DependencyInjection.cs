using System.Text;
using System.Security.Claims;
using System.Threading.RateLimiting;
using AccessCity.API.Common;
using AccessCity.API.Configuration;
using AccessCity.API.Data;
using AccessCity.API.HealthChecks;
using AccessCity.API.Messaging;
using AccessCity.API.Messaging.Kafka;
using AccessCity.API.Models;
using AccessCity.API.Models.Identity;
using AccessCity.API.Modules;
using AccessCity.API.Security;
using AccessCity.API.Services;
using AccessCity.API.Services.Security;
using AccessCity.API.Serialization;
using AccessCity.API.Validators;
using FluentValidation;
using FluentValidation.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using StackExchange.Redis;

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
            services.AddSingleton<KafkaMessageBus>();
            services.AddSingleton<IMessageBus>(sp => sp.GetRequiredService<KafkaMessageBus>());
            services.AddSingleton<IKafkaTopicInitializer>(sp => sp.GetRequiredService<KafkaMessageBus>());
            services.AddHostedService<KafkaTopicWarmupBackgroundService>();
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
        var postgresOptions = configuration.GetSection(PostgresOptions.SectionName).Get<PostgresOptions>() ?? new PostgresOptions();
        var dbContextPoolSize = Math.Max(1, postgresOptions.DbContextPoolSize);

        services.AddDbContextPool<AppDbContext>((serviceProvider, options) =>
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
        }, dbContextPoolSize);

        // ── Bounded-context DbContexts (CQRS database decomposition) ──
        // Each uses a dedicated connection string if configured; otherwise
        // falls back to the shared DefaultConnection for single-database mode.

        services.AddDbContextPool<HazardDbContext>((serviceProvider, options) =>
        {
            var config = serviceProvider.GetRequiredService<IConfiguration>();
            var hazardConn = config.GetConnectionString("HazardDb");
            if (env.IsDevelopment()
                && string.IsNullOrEmpty(hazardConn)
                && string.IsNullOrEmpty(PostgresConnectionStringResolver.Resolve(config))
                && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("USE_POSTGRES")))
            {
                options.UseInMemoryDatabase("AccessCityMemoryDb");
                options.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
            }
            else
            {
                ConfigureNpgsqlForContext<HazardDbContext>(options, config, hazardConn);
            }
        }, Math.Max(1, dbContextPoolSize / 2));

        services.AddDbContextPool<RoutingDbContext>((serviceProvider, options) =>
        {
            var config = serviceProvider.GetRequiredService<IConfiguration>();
            var routingConn = config.GetConnectionString("RoutingDb");
            if (env.IsDevelopment()
                && string.IsNullOrEmpty(routingConn)
                && string.IsNullOrEmpty(PostgresConnectionStringResolver.Resolve(config))
                && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("USE_POSTGRES")))
            {
                options.UseInMemoryDatabase("AccessCityMemoryDb");
                options.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
            }
            else
            {
                ConfigureNpgsqlForContext<RoutingDbContext>(options, config, routingConn);
            }
        }, Math.Max(1, dbContextPoolSize / 2));

        services.AddScoped<IHotPathDbContextFactory, HotPathDbContextFactory>();

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

    /// <summary>
    /// Configures a bounded-context DbContext with an optional dedicated connection string.
    /// Falls back to the shared DefaultConnection when the dedicated string is not configured.
    /// </summary>
    private static void ConfigureNpgsqlForContext<TContext>(
        DbContextOptionsBuilder options,
        IConfiguration configuration,
        string? dedicatedConnectionString) where TContext : DbContext
    {
        var postgresOptions = configuration.GetSection(PostgresOptions.SectionName).Get<PostgresOptions>() ?? new PostgresOptions();
        var connectionString = !string.IsNullOrWhiteSpace(dedicatedConnectionString)
            ? dedicatedConnectionString
            : PostgresConnectionStringResolver.Resolve(configuration, postgresOptions);
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
        services.AddSingleton<AccessCityMetrics>();

        var redisConnection = configuration.GetConnectionString("Redis");
        if (!string.IsNullOrWhiteSpace(redisConnection))
        {
            services.AddSingleton<IConnectionMultiplexer>(_ =>
            {
                var options = ConfigurationOptions.Parse(redisConnection);
                options.AbortOnConnectFail = false;
                return ConnectionMultiplexer.Connect(options);
            });
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
        services.Configure<HotPathWarmupOptions>(configuration.GetSection(HotPathWarmupOptions.SectionName));

        services
            .AddExternalApisModule(configuration)
            .AddHazardsModule(configuration)
            .AddRiskModule()
            .AddRoutingModule(configuration)
            .AddMapsModule(configuration)
            .AddOsmImportModule(configuration);

        services.AddScoped<IAccountService, AccountService>();
        services.AddHostedService<AccessCity.API.Services.Background.HotPathWarmupBackgroundService>();

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
        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IRefreshTokenRevocationService, RefreshTokenRevocationService>();

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

        services.Configure<AccessCityRateLimitingOptions>(
            configuration.GetSection(AccessCityRateLimitingOptions.SectionName));

        // Rate limiting. Global limits are intentionally broad; named endpoint
        // policies shed expensive traffic before it can consume DB, Kafka, or route-worker capacity.
        var rateLimitOptions = configuration
            .GetSection(AccessCityRateLimitingOptions.SectionName)
            .Get<AccessCityRateLimitingOptions>() ?? new AccessCityRateLimitingOptions();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        Math.Ceiling(retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                var endpoint = context.HttpContext.GetEndpoint();
                var policyName = endpoint?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName
                    ?? "global";

                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                if (!context.HttpContext.Response.HasStarted)
                {
                    await context.HttpContext.Response.WriteAsJsonAsync(
                        new ApiError(
                            "Too many requests.",
                            CorrelationId: context.HttpContext.TraceIdentifier,
                            Detail: $"The {policyName} traffic budget is exhausted. Retry after backoff or use the async job endpoint for route computation."),
                        cancellationToken);
                }
            };

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetSlidingWindowLimiter(
                    partitionKey: ResolveRateLimitPartitionKey(context),
                    factory: _ => BuildSlidingWindowOptions(rateLimitOptions.Global)));

            AddPartitionedSlidingPolicy(options, AccessCityRateLimitPolicies.Auth, rateLimitOptions.Auth);
            AddPartitionedSlidingPolicy(options, AccessCityRateLimitPolicies.RoutingHeavy, rateLimitOptions.RoutingHeavy);
            AddPartitionedSlidingPolicy(options, AccessCityRateLimitPolicies.RoutingPoll, rateLimitOptions.RoutingPoll);
            AddPartitionedSlidingPolicy(options, AccessCityRateLimitPolicies.HotRead, rateLimitOptions.HotRead);
            AddPartitionedSlidingPolicy(options, AccessCityRateLimitPolicies.Tile, rateLimitOptions.Tile);
            AddPartitionedSlidingPolicy(options, AccessCityRateLimitPolicies.Write, rateLimitOptions.Write);
            AddPartitionedSlidingPolicy(options, AccessCityRateLimitPolicies.Upload, rateLimitOptions.Upload);
            AddPartitionedSlidingPolicy(options, AccessCityRateLimitPolicies.AiAssist, rateLimitOptions.AiAssist);
        });

        return services;
    }

    private static void AddPartitionedSlidingPolicy(
        RateLimiterOptions options,
        string policyName,
        RateLimitWindowOptions rule)
    {
        options.AddPolicy(policyName, context =>
            RateLimitPartition.GetSlidingWindowLimiter(
                partitionKey: ResolveRateLimitPartitionKey(context),
                factory: _ => BuildSlidingWindowOptions(rule)));
    }

    private static SlidingWindowRateLimiterOptions BuildSlidingWindowOptions(RateLimitWindowOptions rule) =>
        new()
        {
            PermitLimit = Math.Max(1, rule.PermitLimit),
            Window = TimeSpan.FromSeconds(Math.Max(1, rule.WindowSeconds)),
            SegmentsPerWindow = Math.Clamp(rule.SegmentsPerWindow, 1, Math.Max(1, rule.WindowSeconds)),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = Math.Max(0, rule.QueueLimit)
        };

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
            .AddCheck<PostgresHealthCheck>("db", tags: new[] { "ready" })
            .AddCheck<DistributedCacheHealthCheck>("cache", tags: new[] { "ready" })
            .AddCheck<KafkaHealthCheck>("kafka", tags: new[] { "ready" })
            .AddCheck<RouteGraphCoverageHealthCheck>(
                "route_graph",
                failureStatus: configuration.GetValue<bool>("Routing:RequireRouteGraphForReadiness")
                    ? HealthStatus.Unhealthy
                    : HealthStatus.Degraded,
                tags: new[] { "ready" })
            .AddCheck<RouteGraphArtifactManifestHealthCheck>(
                "route_graph_artifacts",
                failureStatus: configuration.GetValue<bool>("Routing:RequireRouteGraphForReadiness")
                    ? HealthStatus.Unhealthy
                    : HealthStatus.Degraded,
                tags: new[] { "ready" });
        services.AddSingleton<CachedReadinessService>();
        services.AddHostedService<ReadinessWarmupBackgroundService>();

        return services;
    }

    // ───────────────────────────── Web / MVC ─────────────────────────────

    public static IServiceCollection AddWebServices(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment env)
    {
        var requestProtection = configuration
            .GetSection(AccessCityRequestProtectionOptions.SectionName)
            .Get<AccessCityRequestProtectionOptions>() ?? new AccessCityRequestProtectionOptions();

        services.Configure<AccessCityRequestProtectionOptions>(
            configuration.GetSection(AccessCityRequestProtectionOptions.SectionName));

        services.Configure<KestrelServerOptions>(options =>
        {
            options.Limits.MaxRequestBodySize = Math.Max(1024 * 1024, requestProtection.MaxRequestBodyBytes);
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(
                Math.Clamp(requestProtection.RequestHeadersTimeoutSeconds, 1, 120));
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(
                Math.Clamp(requestProtection.KeepAliveTimeoutSeconds, 5, 300));

            if (requestProtection.MaxConcurrentConnections is > 0)
            {
                options.Limits.MaxConcurrentConnections = requestProtection.MaxConcurrentConnections;
            }
        });

        services.Configure<FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = Math.Max(1024 * 1024, requestProtection.MaxRequestBodyBytes);
        });

        services.AddRequestTimeouts(options =>
        {
            options.DefaultPolicy = BuildRequestTimeoutPolicy(requestProtection.DefaultTimeoutSeconds);
            options.AddPolicy(
                AccessCityRequestTimeoutPolicies.ShortRead,
                BuildRequestTimeoutPolicy(requestProtection.ShortReadTimeoutSeconds));
            options.AddPolicy(
                AccessCityRequestTimeoutPolicies.RouteSync,
                BuildRequestTimeoutPolicy(requestProtection.RouteSyncTimeoutSeconds));
            options.AddPolicy(
                AccessCityRequestTimeoutPolicies.RouteAsyncSubmit,
                BuildRequestTimeoutPolicy(requestProtection.RouteAsyncSubmitTimeoutSeconds));
            options.AddPolicy(
                AccessCityRequestTimeoutPolicies.AiAssist,
                BuildRequestTimeoutPolicy(requestProtection.AiAssistTimeoutSeconds));
            options.AddPolicy(
                AccessCityRequestTimeoutPolicies.Upload,
                BuildRequestTimeoutPolicy(requestProtection.UploadTimeoutSeconds));
        });

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
            options.JsonSerializerOptions.Converters.Add(new CoordinateJsonConverter());
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

    private static RequestTimeoutPolicy BuildRequestTimeoutPolicy(int timeoutSeconds) =>
        new()
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)),
            TimeoutStatusCode = StatusCodes.Status503ServiceUnavailable,
            WriteTimeoutResponse = async context =>
            {
                if (!context.Response.HasStarted)
                {
                    await context.Response.WriteAsJsonAsync(
                        new ApiError(
                            "Request timed out.",
                            CorrelationId: context.TraceIdentifier,
                            Detail: "The request exceeded the production latency budget. Retry with backoff or use the async route job endpoint for heavy route computation."),
                        context.RequestAborted);
                }
            }
        };
}
