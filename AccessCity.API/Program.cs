using System.Text;
using System.Threading.RateLimiting;
using AccessCity.API.Configuration;
using AccessCity.API.Data;
using AccessCity.API.Models;
using AccessCity.API.Models.Identity;
using AccessCity.API.Services;
using AccessCity.API.Services.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.IdentityModel.Tokens;

EnvironmentBootstrap.LoadRepoRootDotEnv();

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<PostgresOptions>(builder.Configuration.GetSection(PostgresOptions.SectionName));
builder.Services.Configure<OsmImportOptions>(builder.Configuration.GetSection(OsmImportOptions.SectionName));

builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
});

builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();
#pragma warning disable EXTEXP0018
builder.Services.AddHybridCache(options =>
{
    options.DefaultEntryOptions = new HybridCacheEntryOptions
    {
        Expiration = TimeSpan.FromHours(1)
    };
});
#pragma warning restore EXTEXP0018

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        var factory = new NetTopologySuite.IO.Converters.GeoJsonConverterFactory();
        options.JsonSerializerOptions.Converters.Add(factory);
        options.JsonSerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals;
    });

builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
{
    ConfigureDatabase(options, serviceProvider.GetRequiredService<IConfiguration>());
});

builder.Services.AddIdentityCore<AccessCityUser>(options =>
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

builder.Services.AddScoped<IPasswordHasher<AccessCityUser>, Argon2PasswordHasher<AccessCityUser>>();

builder.Configuration["Jwt:Key"] ??= "AccessCity_Secret_Key_Secure_Long_Enough_For_HS512_2026_Development_Phase_64_Bytes_Long_!!!_STILL_ENFORCING_LENGTH_HE_HE";
builder.Configuration["Jwt:Issuer"] ??= "AccessCity.API";
builder.Configuration["Jwt:Audience"] ??= "AccessCity.App";
builder.Configuration["Jwt:AccessTokenExpirationMinutes"] ??= "60";
builder.Configuration["Jwt:RefreshTokenExpirationDays"] ??= "7";

var jwtKey = builder.Configuration["Jwt:Key"] ?? throw new InvalidOperationException("JWT key is not configured.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"],
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddSingleton<ISpatialCacheService, SpatialCacheService>();
builder.Services.AddSingleton<IBloomFilterService, BloomFilterService>();
builder.Services.AddScoped<IMapTileService, MapTileService>();
builder.Services.AddScoped<IRouteGraphRepository, RouteGraphRepository>();
builder.Services.AddScoped<IOsmImportService, OsmImportService>();
builder.Services.AddScoped<RiskScoringService>();
builder.Services.AddScoped<RoutingService>();
builder.Services.AddScoped<ITokenService, TokenService>();

builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("auth", opt =>
    {
        opt.PermitLimit = 5;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 0;
    });
});

builder.Services.AddHttpClient<AccessCity.API.Services.External.IOpenStreetMapClient, AccessCity.API.Services.External.OverpassApiClient>();
builder.Services.AddHttpClient<AccessCity.API.Services.External.IUkPoliceDataClient, AccessCity.API.Services.External.UkPoliceDataClient>();
builder.Services.AddHttpClient<AccessCity.API.Services.External.ISafeHavenPlacesClient, AccessCity.API.Services.External.GooglePlacesClient>();
builder.Services.AddHttpClient<AccessCity.API.Services.External.ILiveHazardClient, AccessCity.API.Services.External.OpenWeatherClient>();

builder.Services.AddOpenApi();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

await MigrateDatabaseAsync(app);
await NormalizeSchemaAsync(app);
await RunOptionalOsmImportAsync(app);

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();

app.Run();

static void ConfigureDatabase(DbContextOptionsBuilder options, IConfiguration configuration)
{
    var connectionString = PostgresConnectionStringResolver.Resolve(configuration);
    var migrationsHistorySchema = PostgresConnectionStringResolver.GetPrimarySearchPath(connectionString);

    options.ConfigureWarnings(warnings =>
    {
        warnings.Ignore(RelationalEventId.PendingModelChangesWarning);
    });

    options.UseNpgsql(connectionString, npgsql =>
    {
        npgsql.UseNetTopologySuite();
        npgsql.MapEnum<DatabaseHazardStatus>("hazard_status");
        if (!string.IsNullOrWhiteSpace(migrationsHistorySchema))
        {
            npgsql.MigrationsHistoryTable("__EFMigrationsHistory", migrationsHistorySchema);
        }
    });
}

static async Task MigrateDatabaseAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var postgresOptions = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<PostgresOptions>>();
    if (!postgresOptions.Value.AutoMigrate)
    {
        return;
    }

    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await dbContext.Database.MigrateAsync();
}

static async Task RunOptionalOsmImportAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var osmOptions = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<OsmImportOptions>>();
    if (!osmOptions.Value.ImportOnStartup)
    {
        return;
    }

    var importer = scope.ServiceProvider.GetRequiredService<IOsmImportService>();
    await importer.ImportConfiguredAsync();
}

static async Task NormalizeSchemaAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    await dbContext.Database.ExecuteSqlRawAsync(
        """
        DO $$
        BEGIN
            IF EXISTS (
                SELECT 1
                FROM information_schema.tables
                WHERE table_schema = 'public'
                  AND table_name = 'hazard_report'
            ) THEN
                IF EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'hazard_report'
                      AND column_name = 'PhotoUrl'
                ) AND NOT EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'hazard_report'
                      AND column_name = 'photo_url'
                ) THEN
                    ALTER TABLE public.hazard_report RENAME COLUMN "PhotoUrl" TO photo_url;
                ELSIF NOT EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'hazard_report'
                      AND column_name = 'photo_url'
                ) THEN
                    ALTER TABLE public.hazard_report
                        ADD COLUMN photo_url text NOT NULL DEFAULT '';

                    ALTER TABLE public.hazard_report
                        ALTER COLUMN photo_url DROP DEFAULT;
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'hazard_report'
                      AND column_name = 'ReporterUserId'
                ) AND NOT EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'hazard_report'
                      AND column_name = 'reporter_user_id'
                ) THEN
                    ALTER TABLE public.hazard_report RENAME COLUMN "ReporterUserId" TO reporter_user_id;
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'hazard_report'
                      AND column_name = 'reporter_user_id'
                ) THEN
                    ALTER TABLE public.hazard_report
                        ALTER COLUMN reporter_user_id TYPE uuid
                        USING NULLIF(reporter_user_id::text, '')::uuid;
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'hazard_report'
                      AND column_name = 'Type'
                ) AND NOT EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'hazard_report'
                      AND column_name = 'hazard_type'
                ) THEN
                    ALTER TABLE public.hazard_report RENAME COLUMN "Type" TO hazard_type;
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'hazard_report'
                      AND column_name = 'hazard_type'
                ) THEN
                    ALTER TABLE public.hazard_report
                        ALTER COLUMN hazard_type DROP DEFAULT;

                    ALTER TABLE public.hazard_report
                        ALTER COLUMN hazard_type TYPE text
                        USING hazard_type::text;
                END IF;

                IF NOT EXISTS (
                    SELECT 1
                    FROM pg_type
                    WHERE typname = 'hazard_status'
                ) THEN
                    CREATE TYPE hazard_status AS ENUM (
                        'reported',
                        'under_review',
                        'verified',
                        'action_planned',
                        'in_progress',
                        'resolved',
                        'rejected',
                        'duplicate'
                    );
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'hazard_report'
                      AND column_name = 'Status'
                ) AND NOT EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'hazard_report'
                      AND column_name = 'status'
                ) THEN
                    ALTER TABLE public.hazard_report RENAME COLUMN "Status" TO status;
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'hazard_report'
                      AND column_name = 'status'
                ) THEN
                    ALTER TABLE public.hazard_report
                        ALTER COLUMN status DROP DEFAULT;

                    ALTER TABLE public.hazard_report
                        ALTER COLUMN status TYPE hazard_status
                        USING CASE lower(status::text)
                            WHEN 'reported' THEN 'reported'::hazard_status
                            WHEN 'under_review' THEN 'under_review'::hazard_status
                            WHEN 'underreview' THEN 'under_review'::hazard_status
                            WHEN 'verified' THEN 'verified'::hazard_status
                            WHEN 'action_planned' THEN 'action_planned'::hazard_status
                            WHEN 'actionplanned' THEN 'action_planned'::hazard_status
                            WHEN 'in_progress' THEN 'in_progress'::hazard_status
                            WHEN 'inprogress' THEN 'in_progress'::hazard_status
                            WHEN 'resolved' THEN 'resolved'::hazard_status
                            WHEN 'dismissed' THEN 'rejected'::hazard_status
                            WHEN 'rejected' THEN 'rejected'::hazard_status
                            WHEN 'duplicate' THEN 'duplicate'::hazard_status
                            ELSE 'reported'::hazard_status
                        END;
                END IF;
            END IF;

            IF EXISTS (
                SELECT 1
                FROM information_schema.tables
                WHERE table_schema = 'public'
                  AND table_name = 'refresh_tokens'
            ) AND NOT EXISTS (
                SELECT 1
                FROM information_schema.tables
                WHERE table_schema = 'public'
                  AND table_name = 'refresh_token'
            ) THEN
                ALTER TABLE public.refresh_tokens RENAME TO refresh_token;
            ELSIF NOT EXISTS (
                SELECT 1
                FROM information_schema.tables
                WHERE table_schema = 'public'
                  AND table_name = 'refresh_token'
            ) THEN
                CREATE TABLE public.refresh_token
                (
                    "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                    token character varying(400) NOT NULL,
                    created_by_ip text NOT NULL DEFAULT '',
                    expires_at timestamp with time zone NOT NULL,
                    created_at timestamp with time zone NOT NULL DEFAULT now(),
                    revoked timestamp with time zone NULL,
                    revoked_by_ip text NULL,
                    replaced_by_token text NULL,
                    reason_revoked text NULL,
                    user_id text NOT NULL,
                    CONSTRAINT "PK_refresh_token" PRIMARY KEY ("Id"),
                    CONSTRAINT "FK_refresh_token_AspNetUsers_user_id"
                        FOREIGN KEY (user_id)
                        REFERENCES public."AspNetUsers" ("Id")
                        ON DELETE CASCADE
                );
            END IF;

            IF EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = 'refresh_token'
                  AND column_name = 'Token'
            ) AND NOT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = 'refresh_token'
                  AND column_name = 'token'
            ) THEN
                ALTER TABLE public.refresh_token RENAME COLUMN "Token" TO token;
            END IF;

            IF EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = 'refresh_token'
                  AND column_name = 'UserId'
            ) AND NOT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = 'refresh_token'
                  AND column_name = 'user_id'
            ) THEN
                ALTER TABLE public.refresh_token RENAME COLUMN "UserId" TO user_id;
            END IF;

            IF EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = 'refresh_token'
                  AND column_name = 'Expires'
            ) AND NOT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = 'refresh_token'
                  AND column_name = 'expires_at'
            ) THEN
                ALTER TABLE public.refresh_token RENAME COLUMN "Expires" TO expires_at;
            END IF;

            IF EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = 'refresh_token'
                  AND column_name = 'Revoked'
            ) AND NOT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = 'refresh_token'
                  AND column_name = 'revoked'
            ) THEN
                ALTER TABLE public.refresh_token RENAME COLUMN "Revoked" TO revoked;
            END IF;
        END $$;
        """);
}

public partial class Program { }
