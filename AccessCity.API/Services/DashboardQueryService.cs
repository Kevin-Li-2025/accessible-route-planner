using AccessCity.API.Data;
using AccessCity.API.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace AccessCity.API.Services;

public interface IDashboardQueryService
{
    Task<DashboardSummary> GetSummaryAsync(CancellationToken cancellationToken);

    Task<object> GetHeatMapAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<InfrastructureFeedItem>> GetInfrastructureFeedAsync(
        int limit,
        CancellationToken cancellationToken);
}

public sealed record DashboardSummary(
    int TotalHazards,
    int ActiveUsers,
    string ActiveUsersDefinition,
    int PendingAlerts,
    int Resolved);

public sealed record InfrastructureFeedItem(
    Guid Id,
    string Type,
    string Description,
    string Status,
    DateTime ReportedAt,
    double[]? Coordinates);

public sealed class DashboardQueryService : IDashboardQueryService
{
    private static readonly HybridCacheEntryOptions SummaryCacheOptions = new()
    {
        Expiration = TimeSpan.FromSeconds(10)
    };

    private readonly IRealHazardDataService _realHazardData;
    private readonly AppDbContext _dbContext;
    private readonly HybridCache _cache;

    public DashboardQueryService(IRealHazardDataService realHazardData, AppDbContext dbContext, HybridCache cache)
    {
        _realHazardData = realHazardData;
        _dbContext = dbContext;
        _cache = cache;
    }

    public async Task<DashboardSummary> GetSummaryAsync(CancellationToken cancellationToken)
    {
#pragma warning disable EXTEXP0018
        return await _cache.GetOrCreateAsync(
            "dashboard:summary:v1",
            async token => await BuildSummaryAsync(token),
            SummaryCacheOptions,
            cancellationToken: cancellationToken);
#pragma warning restore EXTEXP0018
    }

    private async Task<DashboardSummary> BuildSummaryAsync(CancellationToken cancellationToken)
    {
        if (string.Equals(
            _dbContext.Database.ProviderName,
            "Npgsql.EntityFrameworkCore.PostgreSQL",
            StringComparison.Ordinal))
        {
            return await BuildPostgresSummaryAsync(cancellationToken);
        }

        return await BuildEfSummaryAsync(cancellationToken);
    }

    private async Task<DashboardSummary> BuildPostgresSummaryAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var row = await _dbContext.Database
            .SqlQueryRaw<DashboardSummaryRow>(
                """
                SELECT
                    COUNT(*)::int AS "TotalHazards",
                    COUNT(*) FILTER (
                        WHERE status IN ('reported'::hazard_status, 'under_review'::hazard_status)
                    )::int AS "PendingAlerts",
                    COUNT(*) FILTER (
                        WHERE status = 'resolved'::hazard_status
                    )::int AS "Resolved",
                    COALESCE((
                        SELECT COUNT(DISTINCT user_id)::int
                        FROM public.refresh_token
                        WHERE revoked IS NULL
                          AND expires_at > {0}
                    ), 0) AS "ActiveUsers"
                FROM public.hazard_report
                """,
                now)
            .SingleAsync(cancellationToken);

        return new DashboardSummary(
            row.TotalHazards,
            row.ActiveUsers,
            "Distinct accounts with at least one non-revoked, non-expired refresh token.",
            row.PendingAlerts,
            row.Resolved);
    }

    private async Task<DashboardSummary> BuildEfSummaryAsync(CancellationToken cancellationToken)
    {
        var hazardCounts = await _dbContext.Hazards.AsNoTracking()
            .GroupBy(h => h.Status)
            .Select(g => new HazardStatusCount(g.Key, g.Count()))
            .ToListAsync(cancellationToken);
        var totalHazards = hazardCounts.Sum(h => h.Count);
        var pendingAlerts = hazardCounts
            .Where(h => h.Status is HazardStatus.Reported or HazardStatus.UnderReview)
            .Sum(h => h.Count);
        var resolved = hazardCounts
            .Where(h => h.Status == HazardStatus.Resolved)
            .Sum(h => h.Count);

        var now = DateTime.UtcNow;
        var activeUsers = await _dbContext.RefreshTokens.AsNoTracking()
            .Where(t => t.Revoked == null && t.Expires > now)
            .Select(t => t.UserId)
            .Distinct()
            .CountAsync(cancellationToken);

        return new DashboardSummary(
            totalHazards,
            activeUsers,
            "Distinct accounts with at least one non-revoked, non-expired refresh token.",
            pendingAlerts,
            resolved);
    }

    public async Task<object> GetHeatMapAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var hazards = await _realHazardData.GetActiveHazardsAsync();
        var features = new List<object>();

        foreach (var hazard in hazards)
        {
            if (hazard.Location == null) continue;

            features.Add(new
            {
                type = "Feature",
                geometry = new
                {
                    type = "Point",
                    coordinates = new[] { hazard.Location.X, hazard.Location.Y },
                },
                properties = new
                {
                    id = hazard.Id,
                    type = hazard.Type,
                    status = hazard.Status.ToString(),
                    reportedAt = hazard.ReportedAt,
                },
            });
        }

        return new
        {
            type = "FeatureCollection",
            features,
        };
    }

    public async Task<IReadOnlyList<InfrastructureFeedItem>> GetInfrastructureFeedAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var hazards = await _realHazardData.GetActiveHazardsAsync();

        return hazards
            .OrderByDescending(h => h.ReportedAt)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(h => new InfrastructureFeedItem(
                h.Id,
                h.Type,
                h.Description,
                h.Status.ToString(),
                h.ReportedAt,
                h.Location != null ? new[] { h.Location.X, h.Location.Y } : null))
            .ToList();
    }

    private sealed record HazardStatusCount(HazardStatus Status, int Count);

    private sealed class DashboardSummaryRow
    {
        public int TotalHazards { get; set; }
        public int ActiveUsers { get; set; }
        public int PendingAlerts { get; set; }
        public int Resolved { get; set; }
    }
}
