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

namespace AccessCity.API.Services;

public interface IRouteGraphRepository
{
    Task<RouteGraphData> LoadGraphAsync(Coordinate start, Coordinate end, CancellationToken cancellationToken = default);
}

public sealed class RouteGraphRepository : IRouteGraphRepository
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<RouteGraphData>>> InFlightGraphLoads = new();

    private readonly AppDbContext _dbContext;
    private readonly IMemoryCache _cache;
    private readonly IDistributedCache _distributedCache;
    private readonly AccessCityMetrics _metrics;
    private readonly ILogger<RouteGraphRepository> _logger;
    private readonly RoutingOptions _options;
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new(JsonSerializerDefaults.Web);

    public RouteGraphRepository(
        AppDbContext dbContext,
        IMemoryCache cache,
        IDistributedCache distributedCache,
        AccessCityMetrics metrics,
        IOptions<RoutingOptions> options,
        ILogger<RouteGraphRepository> logger)
    {
        _dbContext = dbContext;
        _cache = cache;
        _distributedCache = distributedCache;
        _metrics = metrics;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<RouteGraphData> LoadGraphAsync(
        Coordinate start,
        Coordinate end,
        CancellationToken cancellationToken = default)
    {
        var edgeLimit = Math.Max(100, _options.MaxRouteGraphEdges);
        var region = ComputeShardRegion(start, end);
        var cacheKey = BuildCacheKey(region, edgeLimit);
        var stopwatch = Stopwatch.StartNew();

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

        var graphData = await LoadGraphRegionAsync(region, edgeLimit, cacheKey, cancellationToken);
        var ttl = TimeSpan.FromSeconds(Math.Max(30, _options.RouteGraphCacheTtlSeconds));
        if (graphData.HasCoverage)
        {
            _cache.Set(cacheKey, graphData, ttl);
            await TrySetDistributedSnapshotAsync(cacheKey, graphData, ttl, cancellationToken);
        }

        return graphData;
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
        var edges = await _dbContext.RouteEdges
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

        var nodes = await _dbContext.RouteNodes
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

    private static string BuildCacheKey(GraphShardRegion region, int edgeLimit) =>
        string.Create(CultureInfo.InvariantCulture,
            $"route_graph:v3:{edgeLimit}:{region.MinLon:F4}:{region.MinLat:F4}:{region.MaxLon:F4}:{region.MaxLat:F4}");

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
        RouteGraphCoordinateSnapshot[]? Geometry);

    private sealed record RouteGraphCoordinateSnapshot(double X, double Y);
}
