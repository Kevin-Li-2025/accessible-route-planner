using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using AccessCity.API.Models;
using AccessCity.API.Models.Identity;

namespace AccessCity.API.Data
{
    public class AppDbContext : IdentityDbContext<AccessCityUser>
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
        public DbSet<HazardReport> Hazards => Set<HazardReport>();

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            builder.Entity<HazardReport>(entity =>
            {
                entity.ToTable("Hazards");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Type).HasMaxLength(50);
                entity.Property(e => e.Description).HasMaxLength(500);
            });

            builder.Entity<AccessCityUser>(entity =>
            {
                entity.ToTable("Users");
                entity.Property(e => e.FullName).HasMaxLength(150);
            });

            builder.Entity<RefreshToken>(entity =>
            {
                entity.ToTable("RefreshTokens");
                entity.HasOne(d => d.User)
                    .WithMany(p => p.RefreshTokens)
                    .HasForeignKey(d => d.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
            });
        }
    }
}
