using System.Data;
using AccessCity.API.Configuration;
using AccessCity.API.Data;
using AccessCity.API.HealthChecks;
using AccessCity.API.Hubs;
using AccessCity.API.Messaging;
using AccessCity.API.Models;
using AccessCity.API.Services;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Scalar.AspNetCore;
using Serilog;

namespace AccessCity.API.Extensions;

public static class WebApplicationExtensions
{
    public static WebApplication ConfigurePipeline(this WebApplication app)
    {
        // Must be first to catch all unhandled exceptions.
        app.UseMiddleware<Filters.GlobalExceptionMiddleware>();
        app.UseForwardedHeaders();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.MapScalarApiReference();
        }

        if (!app.Environment.IsDevelopment())
        {
            app.UseHttpsRedirection();
        }

        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseSerilogRequestLogging();
        app.UseRateLimiter();
        app.MapControllers();
        app.MapHub<HazardAlertHub>("/hubs/hazard-alerts");

        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            Predicate = _ => false
        })
            .DisableRateLimiting();
        app.MapGet("/health/ready", async (CachedReadinessService readiness, CancellationToken cancellationToken) =>
        {
            var report = await readiness.CheckAsync(cancellationToken);
            var statusCode = report.Status == HealthStatus.Unhealthy
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status200OK;
            return Results.Text(report.Status.ToString(), "text/plain", statusCode: statusCode);
        })
            .DisableRateLimiting();

        return app;
    }

    public static async Task InitializeDatabaseAsync(this WebApplication app)
    {
        await MigrateDatabaseAsync(app);

        if (!UsesInMemoryDatabase(app))
        {
            using var scope = app.Services.CreateScope();
            var postgresOptions = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<PostgresOptions>>();
            if (postgresOptions.Value.AutoSchemaMaintenance)
            {
                await NormalizeSchemaAsync(app);
                await EnsurePerformanceIndexesAsync(app);
            }

            await RunOptionalOsmImportAsync(app);
        }
    }

    private static async Task MigrateDatabaseAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (IsInMemoryDatabase(dbContext))
        {
            return;
        }

        var postgresOptions = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<PostgresOptions>>();
        if (!postgresOptions.Value.AutoMigrate)
        {
            return;
        }

        await MarkObsoleteInitialMigrationAsAppliedForEmptyDatabaseAsync(dbContext);
        await EnsureRequiredPostgresExtensionsAsync(dbContext, scope.ServiceProvider);

        try
        {
            await dbContext.Database.MigrateAsync();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DuplicateTable)
        {
            var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseMigration");
            logger.LogWarning(
                ex,
                "EF migration hit an existing table. Continuing with schema normalization for a legacy database.");
        }
    }

    private static async Task EnsureRequiredPostgresExtensionsAsync(
        AppDbContext dbContext,
        IServiceProvider services)
    {
        if (!string.Equals(dbContext.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
        {
            return;
        }

        var connection = dbContext.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
        {
            await connection.OpenAsync();
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'postgis') AS postgis_installed,
                    COALESCE((SELECT rolsuper FROM pg_roles WHERE rolname = current_user), false) AS is_superuser;
                """;

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return;
            }

            var postgisInstalled = reader.GetBoolean(0);
            var isSuperuser = reader.GetBoolean(1);
            if (postgisInstalled || isSuperuser)
            {
                return;
            }

            var message =
                "PostGIS extension is not installed and the configured PostgreSQL user cannot create it. " +
                "Install PostGIS with a database administrator account before running application migrations.";
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseMigration");
            logger.LogCritical(message);
            throw new InvalidOperationException(message);
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task MarkObsoleteInitialMigrationAsAppliedForEmptyDatabaseAsync(AppDbContext dbContext)
    {
        if (!string.Equals(dbContext.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
        {
            return;
        }

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1
                    FROM information_schema.tables
                    WHERE table_schema = 'public'
                      AND table_name = '__EFMigrationsHistory'
                ) AND NOT EXISTS (
                    SELECT 1
                    FROM information_schema.tables
                    WHERE table_schema = 'public'
                      AND table_type = 'BASE TABLE'
                      AND table_name <> 'spatial_ref_sys'
                ) THEN
                    CREATE TABLE public."__EFMigrationsHistory"
                    (
                        "MigrationId" character varying(150) NOT NULL,
                        "ProductVersion" character varying(32) NOT NULL,
                        CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
                    );

                    INSERT INTO public."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES ('20260318182223_InitialCreate', '9.0.0');
                END IF;
            END $$;
            """);
    }

    private static bool UsesInMemoryDatabase(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return IsInMemoryDatabase(dbContext);
    }

    private static bool IsInMemoryDatabase(AppDbContext dbContext) =>
        string.Equals(
            dbContext.Database.ProviderName,
            "Microsoft.EntityFrameworkCore.InMemory",
            StringComparison.Ordinal);

    /// <summary>
    /// Schedules OSM import after the host is running. Publishing <see cref="AccessCity.API.Messaging.OsmImportStartedEvent"/>
    /// during <see cref="InitializeDatabaseAsync"/> runs before <see cref="Services.Background.OsmImportBackgroundService"/>
    /// subscribes, so the in-memory bus drops the event and import never starts.
    /// </summary>
    private static Task RunOptionalOsmImportAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var osmOptions = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<OsmImportOptions>>();
        if (!osmOptions.Value.ImportOnStartup)
        {
            return Task.CompletedTask;
        }

        var lifetime = scope.ServiceProvider.GetRequiredService<IHostApplicationLifetime>();
        var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();

        lifetime.ApplicationStarted.Register(() =>
        {
            _ = RunStartupOsmImportAsync(app, loggerFactory);
        });

        return Task.CompletedTask;
    }

    private static async Task RunStartupOsmImportAsync(WebApplication app, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("OsmImportStartup");
        try
        {
            await Task.Delay(500).ConfigureAwait(false);
            await using var asyncScope = app.Services.CreateAsyncScope();
            var bus = asyncScope.ServiceProvider.GetRequiredService<IMessageBus>();
            var options = asyncScope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<OsmImportOptions>>();
            var filePath = options.Value.FilePath;
            if (string.IsNullOrWhiteSpace(filePath))
            {
                logger.LogWarning("Skipping ImportOnStartup because OsmImport:FilePath is not configured.");
                return;
            }

            var jobId = Guid.NewGuid();
            logger.LogInformation("Queueing configured OSM import job {JobId} (ImportOnStartup)", jobId);
            await bus.PublishAsync(
                new OsmImportStartedEvent(jobId, filePath, "startup", DateTime.UtcNow),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ImportOnStartup OSM import failed");
        }
    }

    private static async Task NormalizeSchemaAsync(WebApplication app)
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
    
                -- Fix naming inconsistencies
                IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'Users' AND table_schema = 'public') AND 
                   NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'AspNetUsers' AND table_schema = 'public') THEN
                    ALTER TABLE public."Users" RENAME TO "AspNetUsers";
                END IF;
    
                IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'RefreshTokens' AND table_schema = 'public') AND 
                   NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE (table_name = 'refresh_token' OR table_name = 'refresh_tokens') AND table_schema = 'public') THEN
                    ALTER TABLE public."RefreshTokens" RENAME TO refresh_token;
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
                ) AND EXISTS (
                    SELECT 1
                    FROM information_schema.tables
                    WHERE table_schema = 'public'
                      AND table_name = 'AspNetUsers'
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
    
                IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'route_edges' AND table_schema = 'public') THEN
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'route_edges' AND column_name = 'Access') THEN
                        ALTER TABLE public.route_edges ADD COLUMN "Access" text;
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'route_edges' AND column_name = 'HasBarrier') THEN
                        ALTER TABLE public.route_edges ADD COLUMN "HasBarrier" boolean NOT NULL DEFAULT false;
                    END IF;
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
    
                -- RouteEdge accessibility enhancements
                IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'route_edges') THEN
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'route_edges' AND column_name = 'kerb_height') THEN
                        ALTER TABLE public.route_edges ADD COLUMN kerb_height double precision NOT NULL DEFAULT 0.0;
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'route_edges' AND column_name = 'smoothness') THEN
                        ALTER TABLE public.route_edges ADD COLUMN smoothness character varying(50);
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'route_edges' AND column_name = 'width_metres') THEN
                        ALTER TABLE public.route_edges ADD COLUMN width_metres double precision;
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'route_edges' AND column_name = 'has_tactile_paving') THEN
                        ALTER TABLE public.route_edges ADD COLUMN has_tactile_paving boolean NOT NULL DEFAULT false;
                    END IF;
                END IF;

                CREATE TABLE IF NOT EXISTS public.osm_import_jobs
                (
                    id uuid NOT NULL,
                    status character varying(50) NOT NULL,
                    file_path character varying(2048) NOT NULL,
                    city_name character varying(150) NOT NULL,
                    queued_at_utc timestamp with time zone NOT NULL,
                    started_at_utc timestamp with time zone NULL,
                    finished_at_utc timestamp with time zone NULL,
                    attempts integer NOT NULL DEFAULT 0,
                    feed_ingestion_run_id bigint NULL,
                    error_summary text NULL,
                    metadata jsonb NOT NULL DEFAULT '{{}}'::jsonb,
                    CONSTRAINT "PK_osm_import_jobs" PRIMARY KEY (id)
                );

                CREATE TABLE IF NOT EXISTS public.processed_integration_messages
                (
                    id bigint GENERATED BY DEFAULT AS IDENTITY,
                    message_id character varying(100) NOT NULL,
                    topic character varying(250) NOT NULL,
                    consumer_group_id character varying(250) NOT NULL,
                    event_type character varying(250) NOT NULL,
                    processed_at_utc timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_processed_integration_messages" PRIMARY KEY (id)
                );
            END $$;
            """);
    }

    private static async Task EnsurePerformanceIndexesAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE INDEX IF NOT EXISTS "IX_hazard_report_geom_gist"
                ON public.hazard_report USING GIST (geom);

            CREATE INDEX IF NOT EXISTS "IX_hazard_report_status_reported_at"
                ON public.hazard_report (status, reported_at DESC);

            CREATE INDEX IF NOT EXISTS "IX_hazard_report_active_geom_gist"
                ON public.hazard_report USING GIST (geom)
                WHERE status IN ('reported'::hazard_status, 'under_review'::hazard_status);

            CREATE INDEX IF NOT EXISTS "IX_hazard_report_active_geom_geog_gist"
                ON public.hazard_report USING GIST ((geom::geography))
                WHERE status IN ('reported'::hazard_status, 'under_review'::hazard_status);

            CREATE INDEX IF NOT EXISTS "IX_hazard_report_active_reported_at"
                ON public.hazard_report (reported_at DESC)
                WHERE status IN ('reported'::hazard_status, 'under_review'::hazard_status);

            CREATE INDEX IF NOT EXISTS "IX_refresh_token_revoked_expires_user"
                ON public.refresh_token (revoked, expires_at, user_id);

            CREATE INDEX IF NOT EXISTS "IX_infrastructure_assets_geometry_gist"
                ON public.infrastructure_assets USING GIST ("Geometry");

            CREATE INDEX IF NOT EXISTS "IX_infrastructure_assets_geometry_geog_gist"
                ON public.infrastructure_assets USING GIST (("Geometry"::geography));

            CREATE INDEX IF NOT EXISTS "IX_infrastructure_assets_updated_at"
                ON public.infrastructure_assets ("UpdatedAt" DESC);

            CREATE INDEX IF NOT EXISTS "IX_route_edges_geometry_gist"
                ON public.route_edges USING GIST ("Geometry");

            CREATE INDEX IF NOT EXISTS "IX_route_nodes_location_gist"
                ON public.route_nodes USING GIST ("Location");

            CREATE INDEX IF NOT EXISTS "IX_feed_ingestion_runs_source_status_started"
                ON public.feed_ingestion_runs ("SourceType", "Status", "StartedAt" DESC);

            CREATE INDEX IF NOT EXISTS "IX_osm_import_jobs_status_queued"
                ON public.osm_import_jobs (status, queued_at_utc);

            CREATE INDEX IF NOT EXISTS "IX_processed_integration_messages_processed_at"
                ON public.processed_integration_messages (processed_at_utc);

            CREATE UNIQUE INDEX IF NOT EXISTS "UX_processed_integration_messages_identity"
                ON public.processed_integration_messages (message_id, consumer_group_id);

            CREATE INDEX IF NOT EXISTS "IX_hazard_report_reported_at_brin"
                ON public.hazard_report USING BRIN (reported_at);

            CREATE INDEX IF NOT EXISTS "IX_feed_ingestion_runs_started_at_brin"
                ON public.feed_ingestion_runs USING BRIN ("StartedAt");
            """);
    }
}
