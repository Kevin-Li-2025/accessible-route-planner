using System.Text.Json;
using AccessCity.API.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AccessCity.API.Data;

/// <summary>
/// Bounded-context DbContext for routing graph and infrastructure data (read-heavy).
/// Isolated from Identity and Hazard telemetry to allow independent database scaling.
/// Uses NoTracking by default for read-optimized spatial queries.
/// Falls back to the shared DefaultConnection when RoutingDb is not configured.
/// </summary>
public class RoutingDbContext : DbContext
{
    public RoutingDbContext(DbContextOptions<RoutingDbContext> options) : base(options)
    {
        ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
    }

    public DbSet<RouteNode> RouteNodes => Set<RouteNode>();
    public DbSet<RouteEdge> RouteEdges => Set<RouteEdge>();
    public DbSet<InfrastructureAsset> InfrastructureAssets => Set<InfrastructureAsset>();
    public DbSet<FeedIngestionRun> FeedIngestionRuns => Set<FeedIngestionRun>();
    public DbSet<OsmImportJob> OsmImportJobs => Set<OsmImportJob>();
    public DbSet<ProcessedIntegrationMessage> ProcessedIntegrationMessages => Set<ProcessedIntegrationMessage>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        var isRelational = Database.ProviderName != "Microsoft.EntityFrameworkCore.InMemory";

        var jsonDocumentConverter = new ValueConverter<JsonDocument, string>(
            value => value.RootElement.GetRawText(),
            value => JsonDocument.Parse(
                string.IsNullOrWhiteSpace(value) ? "{}" : value,
                new JsonDocumentOptions()));

        var jsonDocumentComparer = new ValueComparer<JsonDocument>(
            (left, right) => left!.RootElement.GetRawText() == right!.RootElement.GetRawText(),
            value => StringComparer.Ordinal.GetHashCode(value.RootElement.GetRawText()),
            value => JsonDocument.Parse(value.RootElement.GetRawText(), new JsonDocumentOptions()));

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
}
