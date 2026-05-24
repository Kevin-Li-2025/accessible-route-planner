using System.Diagnostics;
using AccessCity.API.Configuration;
using AccessCity.API.Data;
using AccessCity.API.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;

namespace AccessCity.API.Services;

/// <summary>
/// In-memory spatial index for active hazard reports.
/// Uses a NetTopologySuite STRtree (Sort-Tile-Recursive R-Tree) for O(log N + K)
/// spatial queries instead of O(N) linear scans through the full hazard list.
///
/// The index is rebuilt periodically (default: every 30 seconds) from the database
/// by <see cref="HazardSpatialIndexRefreshBackgroundService"/>. This ensures that
/// the A* routing hot-path NEVER hits Postgres for hazard data.
/// </summary>
public interface IHazardSpatialIndex
{
    /// <summary>
    /// Returns all active hazards within the specified radius of the given point.
    /// Uses R-Tree spatial query: O(log N + K) where K is the result count.
    /// </summary>
    IReadOnlyList<HazardReport> QueryNearby(double latitude, double longitude, double radiusMetres);

    /// <summary>
    /// Returns all active hazards whose locations fall within the specified bounding box.
    /// </summary>
    IReadOnlyList<HazardReport> QueryBoundingBox(double minLon, double minLat, double maxLon, double maxLat);

    /// <summary>
    /// Returns all active hazards currently in the index.
    /// </summary>
    IReadOnlyList<HazardReport> GetAllActive();

    /// <summary>
    /// Returns the number of hazards in the current index.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Returns true if the index has been populated at least once.
    /// </summary>
    bool IsWarmedUp { get; }

    /// <summary>
    /// Returns true when this pod knows a write happened after the last rebuild.
    /// Query services should fall back to the authoritative store while this is true.
    /// </summary>
    bool RequiresAuthoritativeRefresh { get; }

    /// <summary>
    /// Marks the snapshot as stale after a local hazard write.
    /// </summary>
    void MarkStale();
}

public sealed class HazardSpatialIndex : IHazardSpatialIndex
{
    // Buffer in degrees (~330m) to expand hazard point envelopes in the R-Tree.
    // This ensures that queries with typical routing radii (100-300m) reliably
    // return nearby hazards even when the point is near the edge of a grid cell.
    private const double PointBufferDegrees = 0.003;

    private volatile HazardSnapshot _snapshot = HazardSnapshot.Empty;
    private volatile bool _requiresAuthoritativeRefresh;

    /// <inheritdoc/>
    public int Count => _snapshot.Hazards.Count;

    /// <inheritdoc/>
    public bool IsWarmedUp => _snapshot.IsWarmedUp;

    /// <inheritdoc/>
    public bool RequiresAuthoritativeRefresh => _requiresAuthoritativeRefresh;

    /// <summary>
    /// Atomically replaces the current spatial index with a new one built from
    /// the provided hazard list. Thread-safe: concurrent readers see either the
    /// old or new snapshot, never a partially-built tree.
    /// </summary>
    public void Rebuild(IReadOnlyList<HazardReport> hazards)
    {
        var tree = new STRtree<HazardReport>();
        foreach (var hazard in hazards)
        {
            if (hazard.Location is null) continue;
            var envelope = new Envelope(hazard.Location.Coordinate);
            envelope.ExpandBy(PointBufferDegrees);
            tree.Insert(envelope, hazard);
        }
        tree.Build();

        _snapshot = new HazardSnapshot(tree, hazards, true);
        _requiresAuthoritativeRefresh = false;
    }

    /// <inheritdoc/>
    public void MarkStale()
    {
        _requiresAuthoritativeRefresh = true;
    }

    /// <inheritdoc/>
    public IReadOnlyList<HazardReport> QueryNearby(double latitude, double longitude, double radiusMetres)
    {
        var snapshot = _snapshot;
        if (!snapshot.IsWarmedUp || snapshot.Tree is null)
        {
            return Array.Empty<HazardReport>();
        }

        var degreesApprox = radiusMetres / 111_320.0;
        var queryEnvelope = new Envelope(
            longitude - degreesApprox, longitude + degreesApprox,
            latitude - degreesApprox, latitude + degreesApprox);

        var candidates = snapshot.Tree.Query(queryEnvelope);

        // Post-filter with precise Haversine distance
        var results = new List<HazardReport>();
        foreach (var hazard in candidates)
        {
            if (hazard.Location is null) continue;
            var dist = RiskScoringService.HaversineDistance(
                latitude, longitude, hazard.Location.Y, hazard.Location.X);
            if (dist <= radiusMetres)
            {
                results.Add(hazard);
            }
        }
        return results;
    }

    /// <inheritdoc/>
    public IReadOnlyList<HazardReport> QueryBoundingBox(double minLon, double minLat, double maxLon, double maxLat)
    {
        var snapshot = _snapshot;
        if (!snapshot.IsWarmedUp || snapshot.Tree is null)
        {
            return Array.Empty<HazardReport>();
        }

        var queryEnvelope = new Envelope(minLon, maxLon, minLat, maxLat);
        var candidates = snapshot.Tree.Query(queryEnvelope);

        // Post-filter to exact bbox (R-Tree may return false positives from the buffer)
        var results = new List<HazardReport>();
        foreach (var hazard in candidates)
        {
            if (hazard.Location is null) continue;
            if (hazard.Location.X >= minLon && hazard.Location.X <= maxLon
                && hazard.Location.Y >= minLat && hazard.Location.Y <= maxLat)
            {
                results.Add(hazard);
            }
        }
        return results;
    }

    /// <inheritdoc/>
    public IReadOnlyList<HazardReport> GetAllActive()
    {
        return _snapshot.Hazards;
    }

    private sealed record HazardSnapshot(
        STRtree<HazardReport>? Tree,
        IReadOnlyList<HazardReport> Hazards,
        bool IsWarmedUp)
    {
        public static readonly HazardSnapshot Empty =
            new(null, Array.Empty<HazardReport>(), false);
    }
}

/// <summary>
/// Background service that periodically refreshes the <see cref="HazardSpatialIndex"/>
/// from the database. Runs every 30 seconds by default to keep the in-memory index
/// fresh without requiring real-time event-driven invalidation.
/// </summary>
public sealed class HazardSpatialIndexRefreshBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly HazardSpatialIndex _index;
    private readonly H3HazardRiskGrid _riskGrid;
    private readonly AccessCityMetrics _metrics;
    private readonly ILogger<HazardSpatialIndexRefreshBackgroundService> _logger;
    private readonly TimeSpan _refreshInterval;

    public HazardSpatialIndexRefreshBackgroundService(
        IServiceScopeFactory scopeFactory,
        IHazardSpatialIndex index,
        IHazardRiskGrid riskGrid,
        AccessCityMetrics metrics,
        IConfiguration configuration,
        ILogger<HazardSpatialIndexRefreshBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _index = (HazardSpatialIndex)index;
        _riskGrid = (H3HazardRiskGrid)riskGrid;
        _metrics = metrics;
        _logger = logger;
        var intervalSeconds = configuration.GetValue("Routing:HazardIndexRefreshIntervalSeconds", 30);
        _refreshInterval = TimeSpan.FromSeconds(Math.Max(5, intervalSeconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial warmup — do this immediately on startup.
        await RefreshIndexAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_refreshInterval, stoppingToken);
                await RefreshIndexAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Hazard spatial index refresh failed; will retry in {Interval}", _refreshInterval);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task RefreshIndexAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<HazardDbContext>();

        var hazards = await dbContext.Hazards
            .Where(h => h.Status == HazardStatus.Reported || h.Status == HazardStatus.UnderReview)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        _index.Rebuild(hazards);

        // Rebuild the risk grid after the R-Tree so it can use R-Tree spatial queries.
        _riskGrid.Rebuild(_index);

        stopwatch.Stop();

        _logger.LogDebug(
            "Hazard spatial index + risk grid refreshed: {Count} active hazards indexed in {ElapsedMs:F1}ms",
            hazards.Count,
            stopwatch.Elapsed.TotalMilliseconds);
    }
}
