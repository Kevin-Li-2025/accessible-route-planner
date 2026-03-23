using AccessCity.API.Configuration;
using AccessCity.API.Data;
using AccessCity.API.Hubs;
using AccessCity.API.Models;
using AccessCity.API.Services;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scalar.AspNetCore;
using Serilog;

namespace AccessCity.API.Extensions;

public static class WebApplicationExtensions
{
    public static WebApplication ConfigurePipeline(this WebApplication app)
    {
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
        app.UseRateLimiter();
        app.UseSerilogRequestLogging();
        app.MapControllers();
        app.MapHub<HazardAlertHub>("/hubs/hazard-alerts");
        
        app.MapHealthChecks("/health").DisableRateLimiting();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
            {
                Predicate = check => check.Tags.Contains("ready")
            })
            .DisableRateLimiting();

        return app;
    }

    public static async Task InitializeDatabaseAsync(this WebApplication app)
    {
        await MigrateDatabaseAsync(app);

        if (!(app.Environment.IsDevelopment() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("USE_POSTGRES"))))
        {
            await NormalizeSchemaAsync(app);
            await RunOptionalOsmImportAsync(app);
        }
    }

    private static async Task MigrateDatabaseAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        if (env.IsDevelopment() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("USE_POSTGRES")))
        {
            return;
        }

        var postgresOptions = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<PostgresOptions>>();
        if (!postgresOptions.Value.AutoMigrate)
        {
            return;
        }

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await dbContext.Database.MigrateAsync();
    }

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
            var import = asyncScope.ServiceProvider.GetRequiredService<IOsmImportService>();
            logger.LogInformation("Running configured OSM import (ImportOnStartup)");
            await import.ImportConfiguredAsync(CancellationToken.None).ConfigureAwait(false);
            logger.LogInformation("ImportOnStartup OSM import finished");
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
            END $$;
            """);
    }
}
