using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AccessCity.API.Configuration;
using AccessCity.API.Data;
using AccessCity.API.Models;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using StackExchange.Redis;

namespace AccessCity.API.Services;

public interface IRouteGraphRepository
{
    Task<RouteGraphData> LoadGraphAsync(Coordinate start, Coordinate end, CancellationToken cancellationToken = default);
}

public sealed class RouteGraphRepository : IRouteGraphRepository
{
    private const string ReleaseLoadLockScript =
        "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";

    private static readonly ConcurrentDictionary<string, Lazy<Task<RouteGraphData>>> InFlightGraphLoads = new();

    private readonly AppDbContext _dbContext;
    private readonly IMemoryCache _cache;
    private readonly IDistributedCache _distributedCache;
    private readonly AccessCityMetrics _metrics;
    private readonly IRouteGraphStatusService _routeGraphStatus;
    private readonly ILogger<RouteGraphRepository> _logger;
    private readonly RoutingOptions _options;
    private readonly IConnectionMultiplexer? _redis;
    private readonly IHotPathDbContextFactory? _hotPathDbContextFactory;
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new(JsonSerializerDefaults.Web);

    public RouteGraphRepository(
        AppDbContext dbContext,
        IMemoryCache cache,
        IDistributedCache distributedCache,
        AccessCityMetrics metrics,
        IRouteGraphStatusService routeGraphStatus,
        IOptions<RoutingOptions> options,
        ILogger<RouteGraphRepository> logger,
        IConnectionMultiplexer? redis = null,
        IHotPathDbContextFactory? hotPathDbContextFactory = null)
    {
        _dbContext = dbContext;
        _cache = cache;
        _distributedCache = distributedCache;
        _metrics = metrics;
        _routeGraphStatus = routeGraphStatus;
        _options = options.Value;
        _logger = logger;
        _redis = redis;
        _hotPathDbContextFactory = hotPathDbContextFactory;
    }

    public async Task<RouteGraphData> LoadGraphAsync(
        Coordinate start,
        Coordinate end,
        CancellationToken cancellationToken = default)
    {
        var edgeLimit = Math.Max(100, _options.MaxRouteGraphEdges);
        var region = ComputeShardRegion(start, end);
        var stopwatch = Stopwatch.StartNew();
        var coverage = await _routeGraphStatus.GetStatusAsync(cancellationToken);
        var cacheKey = BuildCacheKey(region, edgeLimit, coverage.Version);

        if (!coverage.HasCoverage)
        {
            _metrics.CacheLookup("route_graph", hit: false, stopwatch.Elapsed.TotalMilliseconds);
            return new RouteGraphData { ShardKey = cacheKey };
        }

        if (_cache.TryGetValue(cacheKey, out RouteGraphData? cached) && cached is not null)
        {
            _metrics.CacheLookup("route_graph", hit: true, stopwatch.Elapsed.TotalMilliseconds);
            return cached;
        }

        var lazy = InFlightGraphLoads.GetOrAdd(
            cacheKey,
            _ => new Lazy<Task<RouteGraphData>>(
                () => LoadAndCacheGraphRegionAsync(region, edgeLimit, cacheKey, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            var graphData = await lazy.Value;
            _metrics.CacheLookup("route_graph", hit: false, stopwatch.Elapsed.TotalMilliseconds);
            return graphData;
        }
        finally
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompleted
                                    && InFlightGraphLoads.TryGetValue(cacheKey, out var current)
                                    && ReferenceEquals(current, lazy))
            {
                InFlightGraphLoads.TryRemove(cacheKey, out _);
            }
        }
    }

    private async Task<RouteGraphData> LoadAndCacheGraphRegionAsync(
        GraphShardRegion region,
        int edgeLimit,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        var distributed = await TryGetDistributedSnapshotAsync(cacheKey, cancellationToken);
        if (distributed is not null)
        {
            var distributedTtl = TimeSpan.FromSeconds(Math.Max(30, _options.RouteGraphCacheTtlSeconds));
            _cache.Set(cacheKey, distributed, distributedTtl);
            return distributed;
        }

        var ttl = TimeSpan.FromSeconds(Math.Max(30, _options.RouteGraphCacheTtlSeconds));
        var graphData = await LoadGraphRegionWithDistributedCoalescingAsync(region, edgeLimit, cacheKey, ttl, cancellationToken);
        if (graphData.HasCoverage)
        {
            _cache.Set(cacheKey, graphData, ttl);
            await TrySetDistributedSnapshotAsync(cacheKey, graphData, ttl, cancellationToken);
        }

        return graphData;
    }

    private async Task<RouteGraphData> LoadGraphRegionWithDistributedCoalescingAsync(
        GraphShardRegion region,
        int edgeLimit,
        string cacheKey,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        if (!_options.RouteGraphDistributedLoadCoalescingEnabled || _redis is not { IsConnected: true })
        {
            return await LoadGraphRegionAsync(region, edgeLimit, cacheKey, cancellationToken);
        }

        var database = _redis.GetDatabase();
        var lockKey = $"route_graph:load:{cacheKey}";
        var lockToken = Guid.NewGuid().ToString("N");
        var lockTtl = TimeSpan.FromSeconds(Math.Clamp(_options.RouteGraphDistributedLoadLockTtlSeconds, 1, 30));

        try
        {
            var acquired = await database.StringSetAsync(lockKey, lockToken, lockTtl, When.NotExists);
            if (acquired)
            {
                try
                {
                    var snapshot = await TryGetDistributedSnapshotAsync(cacheKey, cancellationToken);
                    if (snapshot is not null)
                    {
                        return snapshot;
                    }

                    var graphData = await LoadGraphRegionAsync(region, edgeLimit, cacheKey, cancellationToken);
                    if (graphData.HasCoverage)
                    {
                        await TrySetDistributedSnapshotAsync(cacheKey, graphData, ttl, cancellationToken);
                    }

                    return graphData;
                }
                finally
                {
                    await ReleaseDistributedLoadLockAsync(database, lockKey, lockToken);
                }
            }

            var peerSnapshot = await WaitForDistributedSnapshotAsync(cacheKey, cancellationToken);
            if (peerSnapshot is not null)
            {
                return peerSnapshot;
            }
        }
        catch (RedisException ex)
        {
            _logger.LogDebug(ex, "Route graph shard {ShardKey} distributed load coalescing unavailable", cacheKey);
        }

        return await LoadGraphRegionAsync(region, edgeLimit, cacheKey, cancellationToken);
    }

    private async Task<RouteGraphData?> WaitForDistributedSnapshotAsync(
        string cacheKey,
        CancellationToken cancellationToken)
    {
        var waitBudget = TimeSpan.FromMilliseconds(Math.Clamp(
            _options.RouteGraphDistributedLoadWaitMilliseconds,
            0,
            Math.Max(0, _options.SyncSafePathTimeoutSeconds * 1000 - 250)));
        if (waitBudget == TimeSpan.Zero)
        {
            return null;
        }

        var deadline = Stopwatch.GetTimestamp() + (long)(waitBudget.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            var snapshot = await TryGetDistributedSnapshotAsync(cacheKey, cancellationToken);
            if (snapshot is not null)
            {
                return snapshot;
            }
        }

        return null;
    }

    private async Task ReleaseDistributedLoadLockAsync(
        IDatabase database,
        RedisKey lockKey,
        RedisValue lockToken)
    {
        try
        {
            await database.ScriptEvaluateAsync(
                ReleaseLoadLockScript,
                new[] { lockKey },
                new[] { lockToken });
        }
        catch (RedisException ex)
        {
            _logger.LogDebug(ex, "Route graph load lock {LockKey} could not be released", lockKey);
        }
    }

    private async Task<RouteGraphData?> TryGetDistributedSnapshotAsync(
        string cacheKey,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var json = await _distributedCache.GetStringAsync(cacheKey, cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
            {
                _metrics.CacheLookup("route_graph_l2", hit: false, stopwatch.Elapsed.TotalMilliseconds);
                return null;
            }

            var snapshot = JsonSerializer.Deserialize<RouteGraphSnapshot>(json, SnapshotJsonOptions);
            if (snapshot is null)
            {
                _metrics.CacheLookup("route_graph_l2", hit: false, stopwatch.Elapsed.TotalMilliseconds);
                return null;
            }

            var graphData = FromSnapshot(snapshot);
            if (!graphData.HasCoverage)
            {
                await _distributedCache.RemoveAsync(cacheKey, cancellationToken);
                _metrics.CacheLookup("route_graph_l2", hit: false, stopwatch.Elapsed.TotalMilliseconds);
                return null;
            }

            _metrics.CacheLookup("route_graph_l2", hit: true, stopwatch.Elapsed.TotalMilliseconds);
            return graphData;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Route graph shard {ShardKey} could not be read from distributed cache", cacheKey);
            _metrics.CacheLookup("route_graph_l2", hit: false, stopwatch.Elapsed.TotalMilliseconds);
            return null;
        }
    }

    private async Task TrySetDistributedSnapshotAsync(
        string cacheKey,
        RouteGraphData graphData,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        if (!graphData.HasCoverage)
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(ToSnapshot(graphData), SnapshotJsonOptions);
            await _distributedCache.SetStringAsync(
                cacheKey,
                json,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Route graph shard {ShardKey} could not be written to distributed cache", cacheKey);
        }
    }

    private async Task<RouteGraphData> LoadGraphRegionAsync(
        GraphShardRegion region,
        int edgeLimit,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        await using var dbLease = _hotPathDbContextFactory?.CreateDbContext()
                                  ?? HotPathDbContextLease.Borrowed(_dbContext);
        var dbContext = dbLease.Context;

        var edges = await dbContext.RouteEdges
            .FromSqlInterpolated($"""
                SELECT *
                FROM route_edges
                WHERE ST_Intersects(
                    "Geometry",
                    ST_MakeEnvelope({region.MinLon}, {region.MinLat}, {region.MaxLon}, {region.MaxLat}, 4326))
                ORDER BY "Geometry" <-> ST_Centroid(ST_MakeEnvelope({region.MinLon}, {region.MinLat}, {region.MaxLon}, {region.MaxLat}, 4326))
                LIMIT {edgeLimit + 1}
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        if (edges.Count == 0)
        {
            return new RouteGraphData();
        }

        var isTruncated = edges.Count > edgeLimit;
        if (isTruncated)
        {
            edges = edges.Take(edgeLimit).ToList();
        }

        var nodeIds = edges
            .SelectMany(edge => new[] { edge.FromNodeId, edge.ToNodeId })
            .Distinct()
            .ToList();

        var nodes = await dbContext.RouteNodes
            .Where(node => nodeIds.Contains(node.Id))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var graph = nodes.ToDictionary(
            node => node.Id,
            node => new GraphNode
            {
                Id = node.Id,
                Location = node.Location.Coordinate
            });

        foreach (var edge in edges)
        {
            if (!graph.TryGetValue(edge.FromNodeId, out var fromNode) ||
                !graph.ContainsKey(edge.ToNodeId))
            {
                continue;
            }

            fromNode.Edges[edge.ToNodeId] = new GraphEdge
            {
                TargetNodeId = edge.ToNodeId,
                DistanceMetres = edge.DistanceMetres,
                BaseSafetyCost = edge.BaseSafetyCost,
                SurfaceType = edge.SurfaceType,
                HasStairs = edge.HasStairs,
                HasCrossing = edge.HasCrossing,
                IsUnderConstruction = edge.IsUnderConstruction,
                LightingQuality = edge.LightingQuality,
                IsSteep = edge.IsSteep,
                KerbHeight = edge.KerbHeight,
                Smoothness = edge.Smoothness,
                WidthMetres = edge.WidthMetres,
                HasTactilePaving = edge.HasTactilePaving,
                HasBarrier = edge.HasBarrier,
                Access = edge.Access,
                AccessibilityCostVersion = edge.AccessibilityCostVersion,
                StandardAccessibilityPenaltySeconds = edge.StandardAccessibilityPenaltySeconds,
                WheelchairAccessibilityPenaltySeconds = edge.WheelchairAccessibilityPenaltySeconds,
                StrollerAccessibilityPenaltySeconds = edge.StrollerAccessibilityPenaltySeconds,
                AccessibilityDataQuality = edge.AccessibilityDataQuality,
                Geometry = edge.Geometry?.Coordinates
            };
        }

        var graphData = new RouteGraphData
        {
            Nodes = graph,
            IsTruncated = isTruncated,
            ShardKey = cacheKey,
            LoadedEdgeCount = edges.Count,
            SpatialBucketSizeDegrees = 0.001
        };
        BuildSpatialBuckets(graphData);

        _logger.LogDebug(
            "Loaded route graph shard {ShardKey}: {NodeCount} nodes, {EdgeCount} edges, truncated={IsTruncated}",
            cacheKey,
            graph.Count,
            edges.Count,
            isTruncated);

        return graphData;
    }

    private static double ComputePaddingDegrees(Coordinate start, Coordinate end)
    {
        var latitudeDelta = Math.Abs(start.Y - end.Y);
        var longitudeDelta = Math.Abs(start.X - end.X);
        return Math.Max(0.01, Math.Max(latitudeDelta, longitudeDelta) * 0.35);
    }

    private GraphShardRegion ComputeShardRegion(Coordinate start, Coordinate end)
    {
        var padding = ComputePaddingDegrees(start, end);
        var minLon = Math.Min(start.X, end.X) - padding;
        var maxLon = Math.Max(start.X, end.X) + padding;
        var minLat = Math.Min(start.Y, end.Y) - padding;
        var maxLat = Math.Max(start.Y, end.Y) + padding;
        var shardSize = Math.Clamp(_options.RouteGraphShardSizeDegrees, 0.002, 0.05);

        return new GraphShardRegion(
            Math.Floor(minLon / shardSize) * shardSize,
            Math.Floor(minLat / shardSize) * shardSize,
            Math.Ceiling(maxLon / shardSize) * shardSize,
            Math.Ceiling(maxLat / shardSize) * shardSize);
    }

    private static string BuildCacheKey(GraphShardRegion region, int edgeLimit, string graphVersion) =>
        string.Create(CultureInfo.InvariantCulture,
            $"route_graph:v5:{graphVersion}:{edgeLimit}:{region.MinLon:F4}:{region.MinLat:F4}:{region.MaxLon:F4}:{region.MaxLat:F4}");

    private static void BuildSpatialBuckets(RouteGraphData graphData)
    {
        var bucketSize = graphData.SpatialBucketSizeDegrees;
        foreach (var node in graphData.Nodes.Values)
        {
            var bucket = (
                X: (int)Math.Floor(node.Location.X / bucketSize),
                Y: (int)Math.Floor(node.Location.Y / bucketSize));
            if (!graphData.SpatialBuckets.TryGetValue(bucket, out var nodeIds))
            {
                nodeIds = new List<long>();
                graphData.SpatialBuckets[bucket] = nodeIds;
            }

            nodeIds.Add(node.Id);
        }
    }

    private static RouteGraphSnapshot ToSnapshot(RouteGraphData graphData)
    {
        var nodes = graphData.Nodes.Values
            .Select(node => new RouteGraphNodeSnapshot(
                node.Id,
                node.Location.X,
                node.Location.Y,
                node.Edges.Values.Select(edge => new RouteGraphEdgeSnapshot(
                    edge.TargetNodeId,
                    edge.DistanceMetres,
                    edge.BaseSafetyCost,
                    edge.SurfaceType,
                    edge.HasStairs,
                    edge.HasCrossing,
                    edge.IsUnderConstruction,
                    edge.LightingQuality,
                    edge.IsSteep,
                    edge.KerbHeight,
                    edge.Smoothness,
                    edge.WidthMetres,
                    edge.HasTactilePaving,
                    edge.HasBarrier,
                    edge.Access,
                    edge.AccessibilityCostVersion,
                    edge.StandardAccessibilityPenaltySeconds,
                    edge.WheelchairAccessibilityPenaltySeconds,
                    edge.StrollerAccessibilityPenaltySeconds,
                    edge.AccessibilityDataQuality,
                    edge.Geometry?.Select(coord => new RouteGraphCoordinateSnapshot(coord.X, coord.Y)).ToArray()))
                    .ToArray()))
            .ToArray();

        return new RouteGraphSnapshot(
            graphData.ShardKey,
            graphData.LoadedEdgeCount,
            graphData.IsTruncated,
            graphData.SpatialBucketSizeDegrees,
            nodes);
    }

    private static RouteGraphData FromSnapshot(RouteGraphSnapshot snapshot)
    {
        var nodes = snapshot.Nodes.ToDictionary(
            node => node.Id,
            node => new GraphNode
            {
                Id = node.Id,
                Location = new Coordinate(node.X, node.Y),
                Edges = node.Edges.ToDictionary(
                    edge => edge.TargetNodeId,
                    edge => new GraphEdge
                    {
                        TargetNodeId = edge.TargetNodeId,
                        DistanceMetres = edge.DistanceMetres,
                        BaseSafetyCost = edge.BaseSafetyCost,
                        SurfaceType = edge.SurfaceType,
                        HasStairs = edge.HasStairs,
                        HasCrossing = edge.HasCrossing,
                        IsUnderConstruction = edge.IsUnderConstruction,
                        LightingQuality = edge.LightingQuality,
                        IsSteep = edge.IsSteep,
                        KerbHeight = edge.KerbHeight,
                        Smoothness = edge.Smoothness,
                        WidthMetres = edge.WidthMetres,
                        HasTactilePaving = edge.HasTactilePaving,
                        HasBarrier = edge.HasBarrier,
                        Access = edge.Access,
                        AccessibilityCostVersion = edge.AccessibilityCostVersion,
                        StandardAccessibilityPenaltySeconds = edge.StandardAccessibilityPenaltySeconds,
                        WheelchairAccessibilityPenaltySeconds = edge.WheelchairAccessibilityPenaltySeconds,
                        StrollerAccessibilityPenaltySeconds = edge.StrollerAccessibilityPenaltySeconds,
                        AccessibilityDataQuality = edge.AccessibilityDataQuality,
                        Geometry = edge.Geometry?.Select(coord => new Coordinate(coord.X, coord.Y)).ToArray()
                    })
            });

        var graphData = new RouteGraphData
        {
            Nodes = nodes,
            IsTruncated = snapshot.IsTruncated,
            ShardKey = snapshot.ShardKey,
            LoadedEdgeCount = snapshot.LoadedEdgeCount,
            SpatialBucketSizeDegrees = snapshot.SpatialBucketSizeDegrees
        };
        BuildSpatialBuckets(graphData);
        return graphData;
    }

    private sealed record GraphShardRegion(double MinLon, double MinLat, double MaxLon, double MaxLat);
    private sealed record RouteGraphSnapshot(
        string? ShardKey,
        int LoadedEdgeCount,
        bool IsTruncated,
        double SpatialBucketSizeDegrees,
        RouteGraphNodeSnapshot[] Nodes);

    private sealed record RouteGraphNodeSnapshot(
        long Id,
        double X,
        double Y,
        RouteGraphEdgeSnapshot[] Edges);

    private sealed record RouteGraphEdgeSnapshot(
        long TargetNodeId,
        double DistanceMetres,
        double BaseSafetyCost,
        string SurfaceType,
        bool HasStairs,
        bool HasCrossing,
        bool IsUnderConstruction,
        double LightingQuality,
        bool IsSteep,
        double KerbHeight,
        string? Smoothness,
        double? WidthMetres,
        bool HasTactilePaving,
        bool HasBarrier,
        string? Access,
        int AccessibilityCostVersion,
        double StandardAccessibilityPenaltySeconds,
        double WheelchairAccessibilityPenaltySeconds,
        double StrollerAccessibilityPenaltySeconds,
        double AccessibilityDataQuality,
        RouteGraphCoordinateSnapshot[]? Geometry);

    private sealed record RouteGraphCoordinateSnapshot(double X, double Y);
}
