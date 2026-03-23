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
                
                if (isRelational)
                {
                    entity.Property(e => e.Geometry).HasColumnType("geometry(LineString,4326)");
                    entity.Property(e => e.Tags).HasColumnType("jsonb");
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
