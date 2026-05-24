using AccessCity.API.Models;
using AccessCity.API.Models.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Text.Json;

namespace AccessCity.API.Data
{
    public class AppDbContext : IdentityDbContext<AccessCityUser>
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
        public DbSet<HazardReport> Hazards => Set<HazardReport>();
        public DbSet<InfrastructureAsset> InfrastructureAssets => Set<InfrastructureAsset>();
        public DbSet<FeedIngestionRun> FeedIngestionRuns => Set<FeedIngestionRun>();
        public DbSet<OsmImportJob> OsmImportJobs => Set<OsmImportJob>();
        public DbSet<ProcessedIntegrationMessage> ProcessedIntegrationMessages => Set<ProcessedIntegrationMessage>();
        public DbSet<RouteNode> RouteNodes => Set<RouteNode>();
        public DbSet<RouteEdge> RouteEdges => Set<RouteEdge>();

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            var preferredRoutesConverter = new ValueConverter<List<string>, string>(
                value => JsonSerializer.Serialize(value, JsonSerializerOptions.Default),
                value => string.IsNullOrWhiteSpace(value)
                    ? new List<string>()
                    : JsonSerializer.Deserialize<List<string>>(value, JsonSerializerOptions.Default) ?? new List<string>());

            var preferredRoutesComparer = new ValueComparer<List<string>>(
                (left, right) => left!.SequenceEqual(right!),
                value => value.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode())),
                value => value.ToList());

            var hazardStatusConverter = new ValueConverter<HazardStatus, DatabaseHazardStatus>(
                value => ConvertHazardStatusToDb(value),
                value => ConvertHazardStatusFromDb(value));

            var nullableGuidStringConverter = new ValueConverter<string?, Guid?>(
                value => ConvertNullableStringToGuid(value),
                value => ConvertNullableGuidToString(value));

            var jsonDocumentConverter = new ValueConverter<JsonDocument, string>(
                value => value.RootElement.GetRawText(),
                value => JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(value) ? "{}" : value,
                    new JsonDocumentOptions()));

            var jsonDocumentComparer = new ValueComparer<JsonDocument>(
                (left, right) => left!.RootElement.GetRawText() == right!.RootElement.GetRawText(),
                value => StringComparer.Ordinal.GetHashCode(value.RootElement.GetRawText()),
                value => JsonDocument.Parse(value.RootElement.GetRawText(), new JsonDocumentOptions()));

            var isRelational = this.Database.ProviderName != "Microsoft.EntityFrameworkCore.InMemory";

            // Note: If Database.ProviderName throws, we might need a different approach.
            // But usually it's fine here.

            builder.Entity<HazardReport>(entity =>
            {
                entity.ToTable("hazard_report");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.Location).HasColumnName("geom");
                if (isRelational) entity.Property(e => e.Location).HasColumnType("geometry(Point,4326)");

                entity.Property(e => e.Type).HasColumnName("hazard_type");
                if (isRelational) entity.Property(e => e.Type).HasColumnType("text");

                entity.Property(e => e.Description).HasColumnName("description");
                entity.Property(e => e.PhotoUrl).HasColumnName("photo_url").HasMaxLength(2048);
                entity.Property(e => e.ReportedAt).HasColumnName("reported_at");
                entity.Property(e => e.Source).HasColumnName("source");

                entity.Property(e => e.Status).HasColumnName("status").HasConversion(hazardStatusConverter);
                if (isRelational) entity.Property(e => e.Status).HasColumnType("hazard_status");

                entity.Property(e => e.ReporterUserId)
                    .HasColumnName("reporter_user_id")
                    .HasConversion(nullableGuidStringConverter);
                if (isRelational) entity.Property(e => e.ReporterUserId).HasColumnType("uuid");

                entity.HasIndex(e => e.ReportedAt).HasDatabaseName("IX_hazard_report_reported_at");
            });

            if (isRelational)
            {
                builder.HasPostgresEnum<DatabaseHazardStatus>("hazard_status");
            }

            builder.Entity<AccessCityUser>(entity =>
            {
                entity.ToTable("AspNetUsers");
                entity.Property(e => e.FullName).HasMaxLength(150);
                entity.Property(e => e.PreferredRoutes)
                    .HasConversion(preferredRoutesConverter)
                    .Metadata.SetValueComparer(preferredRoutesComparer);

                if (isRelational) entity.Property(e => e.PreferredRoutes).HasColumnType("jsonb");
            });

            builder.Entity<RefreshToken>(entity =>
            {
                entity.ToTable("refresh_token");
                entity.Property(e => e.Token).HasColumnName("token").HasMaxLength(400);
                entity.Property(e => e.CreatedByIp).HasColumnName("created_by_ip");
                entity.Property(e => e.Expires).HasColumnName("expires_at");
                entity.Property(e => e.Created).HasColumnName("created_at");
                entity.Property(e => e.Revoked).HasColumnName("revoked");
                entity.Property(e => e.RevokedByIp).HasColumnName("revoked_by_ip");
                entity.Property(e => e.ReplacedByToken).HasColumnName("replaced_by_token");
                entity.Property(e => e.ReasonRevoked).HasColumnName("reason_revoked");
                entity.Property(e => e.UserId).HasColumnName("user_id");

                entity.HasOne(d => d.User)
                    .WithMany(p => p.RefreshTokens)
                    .HasForeignKey(d => d.UserId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasIndex(e => e.Token).HasDatabaseName("IX_refresh_token_token");
            });

            builder.Entity<InfrastructureAsset>(entity =>
            {
                entity.ToTable("infrastructure_assets");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.AssetType).HasMaxLength(100);
                entity.Property(e => e.Status).HasMaxLength(50);
                entity.Property(e => e.SourceSystem).HasMaxLength(100);
                entity.Property(e => e.SourceRecordId).HasMaxLength(250);

                if (isRelational)
                {
                    entity.Property(e => e.Geometry).HasColumnType("geometry(Geometry,4326)");
                    entity.Property(e => e.AccessibilityInfo).HasColumnType("jsonb");
                    entity.Property(e => e.AccessibilityProfile).HasColumnType("jsonb");
                }
                else
                {
                    entity.Property(e => e.AccessibilityInfo)
                        .HasConversion(jsonDocumentConverter)
                        .Metadata.SetValueComparer(jsonDocumentComparer);
                    entity.Property(e => e.AccessibilityProfile)
                        .HasConversion(jsonDocumentConverter)
                        .Metadata.SetValueComparer(jsonDocumentComparer);
                }

                entity.HasIndex(e => new { e.SourceSystem, e.SourceRecordId }).IsUnique();
            });

            builder.Entity<FeedIngestionRun>(entity =>
            {
                entity.ToTable("feed_ingestion_runs");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.SourceType).HasMaxLength(50);
                entity.Property(e => e.SourceName).HasMaxLength(512);
                entity.Property(e => e.Status).HasMaxLength(50);
                if (isRelational) entity.Property(e => e.Metadata).HasColumnType("jsonb");
                else
                {
                    entity.Property(e => e.Metadata)
                        .HasConversion(jsonDocumentConverter)
                        .Metadata.SetValueComparer(jsonDocumentComparer);
                }
            });

            builder.Entity<OsmImportJob>(entity =>
            {
                entity.ToTable("osm_import_jobs");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(50);
                entity.Property(e => e.FilePath).HasColumnName("file_path").HasMaxLength(2048);
                entity.Property(e => e.CityName).HasColumnName("city_name").HasMaxLength(150);
                entity.Property(e => e.QueuedAtUtc).HasColumnName("queued_at_utc");
                entity.Property(e => e.StartedAtUtc).HasColumnName("started_at_utc");
                entity.Property(e => e.FinishedAtUtc).HasColumnName("finished_at_utc");
                entity.Property(e => e.Attempts).HasColumnName("attempts");
                entity.Property(e => e.FeedIngestionRunId).HasColumnName("feed_ingestion_run_id");
                entity.Property(e => e.ErrorSummary).HasColumnName("error_summary");
                if (isRelational) entity.Property(e => e.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
                else
                {
                    entity.Property(e => e.Metadata)
                        .HasColumnName("metadata")
                        .HasConversion(jsonDocumentConverter)
                        .Metadata.SetValueComparer(jsonDocumentComparer);
                }

                entity.HasIndex(e => new { e.Status, e.QueuedAtUtc }).HasDatabaseName("IX_osm_import_jobs_status_queued");
            });

            builder.Entity<ProcessedIntegrationMessage>(entity =>
            {
                entity.ToTable("processed_integration_messages");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.MessageId).HasColumnName("message_id").HasMaxLength(100);
                entity.Property(e => e.Topic).HasColumnName("topic").HasMaxLength(250);
                entity.Property(e => e.ConsumerGroupId).HasColumnName("consumer_group_id").HasMaxLength(250);
                entity.Property(e => e.EventType).HasColumnName("event_type").HasMaxLength(250);
                entity.Property(e => e.ProcessedAtUtc).HasColumnName("processed_at_utc");
                entity.HasIndex(e => new { e.MessageId, e.ConsumerGroupId })
                    .IsUnique()
                    .HasDatabaseName("UX_processed_integration_messages_identity");
                entity.HasIndex(e => e.ProcessedAtUtc).HasDatabaseName("IX_processed_integration_messages_processed_at");
            });

            builder.Entity<RouteNode>(entity =>
            {
                entity.ToTable("route_nodes");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Id).ValueGeneratedNever();
                if (isRelational)
                {
                    entity.Property(e => e.Location).HasColumnType("geometry(Point,4326)");
                    entity.Property(e => e.Tags).HasColumnType("jsonb");
                }
                else
                {
                    entity.Property(e => e.Tags)
                        .HasConversion(jsonDocumentConverter)
                        .Metadata.SetValueComparer(jsonDocumentComparer);
                }
            });

            builder.Entity<RouteEdge>(entity =>
            {
                entity.ToTable("route_edges");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.SurfaceType).HasMaxLength(50);
                entity.Property(e => e.KerbHeight).HasColumnName("kerb_height");
                entity.Property(e => e.Smoothness).HasColumnName("smoothness").HasMaxLength(50);
                entity.Property(e => e.WidthMetres).HasColumnName("width_metres");
                entity.Property(e => e.HasTactilePaving).HasColumnName("has_tactile_paving");
                entity.Property(e => e.AccessibilityCostVersion).HasColumnName("accessibility_cost_version");
                entity.Property(e => e.StandardAccessibilityPenaltySeconds).HasColumnName("standard_accessibility_penalty_seconds");
                entity.Property(e => e.WheelchairAccessibilityPenaltySeconds).HasColumnName("wheelchair_accessibility_penalty_seconds");
                entity.Property(e => e.StrollerAccessibilityPenaltySeconds).HasColumnName("stroller_accessibility_penalty_seconds");
                entity.Property(e => e.AccessibilityDataQuality).HasColumnName("accessibility_data_quality");

                if (isRelational)
                {
                    entity.Property(e => e.Geometry).HasColumnType("geometry(LineString,4326)");
                    entity.Property(e => e.Tags).HasColumnType("jsonb");
                }
                else
                {
                    entity.Property(e => e.Tags)
                        .HasConversion(jsonDocumentConverter)
                        .Metadata.SetValueComparer(jsonDocumentComparer);
                }

                entity.HasIndex(e => new { e.FromNodeId, e.ToNodeId });
                entity.HasOne(e => e.FromNode)
                    .WithMany()
                    .HasForeignKey(e => e.FromNodeId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(e => e.ToNode)
                    .WithMany()
                    .HasForeignKey(e => e.ToNodeId)
                    .OnDelete(DeleteBehavior.Cascade);
            });
        }

        private static DatabaseHazardStatus ConvertHazardStatusToDb(HazardStatus value)
        {
            return value switch
            {
                HazardStatus.Reported => DatabaseHazardStatus.Reported,
                HazardStatus.Acknowledged => DatabaseHazardStatus.Verified,
                HazardStatus.UnderReview => DatabaseHazardStatus.UnderReview,
                HazardStatus.Resolved => DatabaseHazardStatus.Resolved,
                HazardStatus.Dismissed => DatabaseHazardStatus.Rejected,
                _ => DatabaseHazardStatus.Reported
            };
        }

        private static HazardStatus ConvertHazardStatusFromDb(DatabaseHazardStatus value)
        {
            return value switch
            {
                DatabaseHazardStatus.Reported => HazardStatus.Reported,
                DatabaseHazardStatus.UnderReview => HazardStatus.UnderReview,
                DatabaseHazardStatus.Verified => HazardStatus.Acknowledged,
                DatabaseHazardStatus.ActionPlanned => HazardStatus.UnderReview,
                DatabaseHazardStatus.InProgress => HazardStatus.UnderReview,
                DatabaseHazardStatus.Resolved => HazardStatus.Resolved,
                DatabaseHazardStatus.Rejected => HazardStatus.Dismissed,
                DatabaseHazardStatus.Duplicate => HazardStatus.Dismissed,
                _ => HazardStatus.Reported
            };
        }

        private static Guid? ConvertNullableStringToGuid(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return Guid.Parse(value);
        }

        private static string? ConvertNullableGuidToString(Guid? value)
        {
            return value?.ToString();
        }
    }
}
