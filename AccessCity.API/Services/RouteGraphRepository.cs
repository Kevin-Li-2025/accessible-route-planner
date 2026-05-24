using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private readonly IRouteGraphArtifactStore _artifactStore;
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public RouteGraphRepository(
        AppDbContext dbContext,
        IMemoryCache cache,
        IDistributedCache distributedCache,
        AccessCityMetrics metrics,
        IRouteGraphStatusService routeGraphStatus,
        IOptions<RoutingOptions> options,
        ILogger<RouteGraphRepository> logger,
        IRouteGraphArtifactStore artifactStore,
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
        _artifactStore = artifactStore;
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
        var loadRegions = ComputeLoadRegions(region);
        var stopwatch = Stopwatch.StartNew();
        var coverage = await _routeGraphStatus.GetStatusAsync(cancellationToken);
        var cacheKey = BuildCacheKey(
            region,
            edgeLimit,
            coverage.Version,
            loadRegions.Count > 1 ? $"bundle{loadRegions.Count}" : "region");

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
                () => LoadAndCacheGraphRegionsAsync(loadRegions, edgeLimit, cacheKey, coverage.Version, cancellationToken),
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

    private async Task<RouteGraphData> LoadAndCacheGraphRegionsAsync(
        IReadOnlyList<GraphShardRegion> regions,
        int edgeLimit,
        string cacheKey,
        string graphVersion,
        CancellationToken cancellationToken)
    {
        var ttl = TimeSpan.FromSeconds(Math.Max(30, _options.RouteGraphCacheTtlSeconds));
        var distributed = await TryGetDistributedSnapshotAsync(cacheKey, cancellationToken);
        if (distributed is not null)
        {
            _cache.Set(cacheKey, distributed, ttl);
            return distributed;
        }

        if (regions.Count == 1)
        {
            return await LoadAndCacheGraphRegionAsync(regions[0], edgeLimit, cacheKey, ttl, cancellationToken);
        }

        var perShardEdgeLimit = ComputePerShardEdgeLimit(edgeLimit, regions.Count);
        var shards = new List<RouteGraphData>(regions.Count);
        foreach (var region in regions)
        {
            var shardKey = BuildCacheKey(region, perShardEdgeLimit, graphVersion, "cell");
            var shard = await LoadAndCacheGraphRegionAsync(region, perShardEdgeLimit, shardKey, ttl, cancellationToken);
            if (shard.HasCoverage)
            {
                shards.Add(shard);
            }
        }

        var graphData = MergeGraphShards(shards, cacheKey, edgeLimit);
        if (graphData.HasCoverage)
        {
            _cache.Set(cacheKey, graphData, ttl);
            await TrySetDistributedSnapshotAsync(cacheKey, graphData, ttl, cancellationToken);
        }

        return graphData;
    }

    private async Task<RouteGraphData> LoadAndCacheGraphRegionAsync(
        GraphShardRegion region,
        int edgeLimit,
        string cacheKey,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(cacheKey, out RouteGraphData? cached) && cached is not null)
        {
            return cached;
        }

        var distributed = await TryGetDistributedSnapshotAsync(cacheKey, cancellationToken);
        if (distributed is not null)
        {
            _cache.Set(cacheKey, distributed, ttl);
            return distributed;
        }

        var fileArtifact = await TryLoadGraphRegionFromManifestAsync(region, edgeLimit, cacheKey, cancellationToken);
        if (fileArtifact is not null)
        {
            _cache.Set(cacheKey, fileArtifact, ttl);
            await TrySetDistributedSnapshotAsync(cacheKey, fileArtifact, ttl, cancellationToken);
            return fileArtifact;
        }

        var graphData = await LoadGraphRegionWithDistributedCoalescingAsync(region, edgeLimit, cacheKey, ttl, cancellationToken);
        if (graphData.HasCoverage)
        {
            _cache.Set(cacheKey, graphData, ttl);
            await TrySetDistributedSnapshotAsync(cacheKey, graphData, ttl, cancellationToken);
        }

        return graphData;
    }

    private async Task<RouteGraphData?> TryLoadGraphRegionFromManifestAsync(
        GraphShardRegion region,
        int edgeLimit,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        var manifest = await _artifactStore.TryReadManifestAsync(cancellationToken);
        if (manifest is null)
        {
            return null;
        }

        var matchingShards = manifest.Shards
            .Where(shard => Overlaps(region, new GraphShardRegion(shard.MinLon, shard.MinLat, shard.MaxLon, shard.MaxLat)))
            .OrderBy(shard => shard.CacheKey, StringComparer.Ordinal)
            .ToArray();
        if (matchingShards.Length == 0
            || matchingShards.Length > Math.Max(1, _options.RouteGraphMaxFileArtifactShardLoadCount))
        {
            return null;
        }

        var shards = new List<RouteGraphData>(matchingShards.Length);
        foreach (var shard in matchingShards)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var graphData = await TryGetDistributedSnapshotAsync(shard.CacheKey, cancellationToken);
            if (graphData is null)
            {
                var artifact = await _artifactStore.TryReadManifestShardAsync(shard, cancellationToken);
                if (artifact is null)
                {
                    return null;
                }

                graphData = RouteGraphArtifactCodec.Unpack(artifact.Artifact);
            }

            if (!graphData.HasCoverage)
            {
                return null;
            }

            shards.Add(graphData);
        }

        var loaded = shards.Count == 1
            ? shards[0]
            : MergeGraphShards(shards, cacheKey, edgeLimit);
        _logger.LogDebug(
            "Loaded route graph shard {ShardKey} from {ShardCount} manifest artifacts ({NodeCount} nodes, {EdgeCount} edges)",
            cacheKey,
            shards.Count,
            loaded.Nodes.Count,
            loaded.LoadedEdgeCount);
        return loaded;
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
            var payload = await _distributedCache.GetAsync(cacheKey, cancellationToken);
            if (payload is null || payload.Length == 0)
            {
                var artifactStoreGraphData = await TryGetFileArtifactSnapshotAsync(cacheKey, stopwatch, cancellationToken);
                if (artifactStoreGraphData is not null)
                {
                    return artifactStoreGraphData;
                }

                _metrics.CacheLookup("route_graph_l2", hit: false, stopwatch.Elapsed.TotalMilliseconds);
                return null;
            }

            if (_options.RouteGraphPackedArtifactsEnabled
                && RouteGraphArtifactCodec.TryDeserializeRedisPayload(payload, out var artifact))
            {
                if (artifact is not null && RouteGraphArtifactCodec.IsCompatible(artifact))
                {
                    var packedGraphData = RouteGraphArtifactCodec.Unpack(artifact);
                    if (!packedGraphData.HasCoverage)
                    {
                        await _distributedCache.RemoveAsync(cacheKey, cancellationToken);
                        var fileArtifact = await TryGetFileArtifactSnapshotAsync(cacheKey, stopwatch, cancellationToken);
                        if (fileArtifact is not null)
                        {
                            return fileArtifact;
                        }

                        _metrics.CacheLookup("route_graph_l2", hit: false, stopwatch.Elapsed.TotalMilliseconds);
                        return null;
                    }

                    _metrics.CacheLookup("route_graph_l2", hit: true, stopwatch.Elapsed.TotalMilliseconds);
                    return packedGraphData;
                }

                await _distributedCache.RemoveAsync(cacheKey, cancellationToken);
                var fallbackFileArtifact = await TryGetFileArtifactSnapshotAsync(cacheKey, stopwatch, cancellationToken);
                if (fallbackFileArtifact is not null)
                {
                    return fallbackFileArtifact;
                }

                _metrics.CacheLookup("route_graph_l2", hit: false, stopwatch.Elapsed.TotalMilliseconds);
                return null;
            }

            var json = Encoding.UTF8.GetString(payload);
            using var document = JsonDocument.Parse(json);
            var snapshot = document.RootElement.Deserialize<RouteGraphSnapshot>(SnapshotJsonOptions);
            if (snapshot is null)
            {
                var fileArtifact = await TryGetFileArtifactSnapshotAsync(cacheKey, stopwatch, cancellationToken);
                if (fileArtifact is not null)
                {
                    return fileArtifact;
                }

                _metrics.CacheLookup("route_graph_l2", hit: false, stopwatch.Elapsed.TotalMilliseconds);
                return null;
            }

            var graphData = FromSnapshot(snapshot);
            if (!graphData.HasCoverage)
            {
                await _distributedCache.RemoveAsync(cacheKey, cancellationToken);
                var fileArtifact = await TryGetFileArtifactSnapshotAsync(cacheKey, stopwatch, cancellationToken);
                if (fileArtifact is not null)
                {
                    return fileArtifact;
                }

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

    private async Task<RouteGraphData?> TryGetFileArtifactSnapshotAsync(
        string cacheKey,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var fileArtifact = await _artifactStore.TryReadAsync(cacheKey, cancellationToken);
        if (fileArtifact is null)
        {
            return null;
        }

        var graphData = RouteGraphArtifactCodec.Unpack(fileArtifact.Artifact);
        if (!graphData.HasCoverage)
        {
            return null;
        }

        _metrics.CacheLookup("route_graph_file", hit: true, stopwatch.Elapsed.TotalMilliseconds);
        _logger.LogDebug(
            "Loaded route graph shard {ShardKey} from file artifact {ArtifactPath} ({ArtifactBytes} bytes)",
            cacheKey,
            fileArtifact.ArtifactPath,
            fileArtifact.PayloadBytes);
        return graphData;
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
            if (_options.RouteGraphPackedArtifactsEnabled)
            {
                var artifact = RouteGraphArtifactCodec.Pack(graphData);
                var redisPayload = RouteGraphArtifactCodec.SerializeRedisPayload(artifact);
                await _distributedCache.SetAsync(
                    cacheKey,
                    redisPayload,
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
                    cancellationToken);
                if (_options.RouteGraphFileArtifactWriteThroughEnabled)
                {
                    await _artifactStore.WriteAsync(
                        cacheKey,
                        artifact,
                        redisPayload,
                        "route-graph-repository",
                        cancellationToken);
                }
            }
            else
            {
                var json = JsonSerializer.Serialize(ToSnapshot(graphData), SnapshotJsonOptions);
                await _distributedCache.SetStringAsync(
                    cacheKey,
                    json,
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
                    cancellationToken);
            }
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
            .Include(e => e.FromNode)
            .Include(e => e.ToNode)
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

        var graph = new Dictionary<long, GraphNode>();

        foreach (var edge in edges)
        {
            if (edge.FromNode != null && !graph.ContainsKey(edge.FromNodeId))
            {
                graph[edge.FromNodeId] = new GraphNode
                {
                    Id = edge.FromNodeId,
                    Location = edge.FromNode.Location.Coordinate
                };
            }
            if (edge.ToNode != null && !graph.ContainsKey(edge.ToNodeId))
            {
                graph[edge.ToNodeId] = new GraphNode
                {
                    Id = edge.ToNodeId,
                    Location = edge.ToNode.Location.Coordinate
                };
            }

            if (graph.TryGetValue(edge.FromNodeId, out var fromNode) && graph.ContainsKey(edge.ToNodeId))
            {
                fromNode.Edges[edge.ToNodeId] = CreateGraphEdge(edge);
            }
        }

        var graphData = new RouteGraphData
        {
            Nodes = graph,
            IsTruncated = isTruncated,
            ShardKey = cacheKey,
            SourceShardKeys = new[] { cacheKey },
            LoadedEdgeCount = edges.Count,
            SpatialBucketSizeDegrees = 0.001
        };
        BuildSpatialBuckets(graphData);
        RouteGraphPreprocessor.TryAttachPreprocessing(graphData, _options);

        _logger.LogDebug(
            "Loaded route graph shard {ShardKey}: {NodeCount} nodes, {EdgeCount} edges, truncated={IsTruncated}",
            cacheKey,
            graph.Count,
            edges.Count,
            isTruncated);

        return graphData;
    }

    private static GraphEdge CreateGraphEdge(RouteEdge edge)
    {
        var graphEdge = new GraphEdge
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
        RouteEdgeCostModel.PopulateTraversalWeights(graphEdge);
        return graphEdge;
    }

    private static GraphEdge CreateGraphEdge(RouteGraphEdgeSnapshot edge)
    {
        var graphEdge = new GraphEdge
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
        };
        RouteEdgeCostModel.PopulateTraversalWeights(graphEdge);
        return graphEdge;
    }

    private static double ComputePaddingDegrees(Coordinate start, Coordinate end)
    {
        var latitudeDelta = Math.Abs(start.Y - end.Y);
        var longitudeDelta = Math.Abs(start.X - end.X);
        return Math.Max(0.01, Math.Max(latitudeDelta, longitudeDelta) * 0.35);
    }

    private static bool Overlaps(GraphShardRegion a, GraphShardRegion b) =>
        a.MinLon < b.MaxLon
        && a.MaxLon > b.MinLon
        && a.MinLat < b.MaxLat
        && a.MaxLat > b.MinLat;

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

    private IReadOnlyList<GraphShardRegion> ComputeLoadRegions(GraphShardRegion region)
    {
        if (!_options.RouteGraphPrepartitionedShardsEnabled)
        {
            return new[] { region };
        }

        var shardSize = Math.Clamp(_options.RouteGraphShardSizeDegrees, 0.002, 0.05);
        var minX = (int)Math.Floor(region.MinLon / shardSize);
        var minY = (int)Math.Floor(region.MinLat / shardSize);
        var maxX = (int)Math.Ceiling(region.MaxLon / shardSize) - 1;
        var maxY = (int)Math.Ceiling(region.MaxLat / shardSize) - 1;
        var shardCount = (maxX - minX + 1) * (maxY - minY + 1);

        if (shardCount <= 1 || shardCount > Math.Max(1, _options.RouteGraphMaxPrepartitionedShardCount))
        {
            return new[] { region };
        }

        var regions = new List<GraphShardRegion>(shardCount);
        for (var x = minX; x <= maxX; x++)
        {
            for (var y = minY; y <= maxY; y++)
            {
                regions.Add(new GraphShardRegion(
                    x * shardSize,
                    y * shardSize,
                    (x + 1) * shardSize,
                    (y + 1) * shardSize));
            }
        }

        return regions;
    }

    private static int ComputeBasePerShardEdgeLimit(int edgeLimit, int shardCount) =>
        Math.Max(100, Math.Max(1, edgeLimit) / Math.Max(1, shardCount));

    private int ComputePerShardEdgeLimit(int edgeLimit, int shardCount) =>
        Math.Max(_options.RouteGraphMinEdgesPerPrepartitionedShard, ComputeBasePerShardEdgeLimit(edgeLimit, shardCount));

    private RouteGraphData MergeGraphShards(
        IReadOnlyList<RouteGraphData> shards,
        string cacheKey,
        int edgeLimit)
    {
        if (shards.Count == 0)
        {
            return new RouteGraphData { ShardKey = cacheKey };
        }

        var nodes = new Dictionary<long, GraphNode>();
        foreach (var shard in shards)
        {
            foreach (var node in shard.Nodes.Values)
            {
                if (!nodes.TryGetValue(node.Id, out var mergedNode))
                {
                    mergedNode = new GraphNode
                    {
                        Id = node.Id,
                        Location = node.Location
                    };
                    nodes[node.Id] = mergedNode;
                }

                foreach (var (targetNodeId, edge) in node.Edges)
                {
                    mergedNode.Edges[targetNodeId] = edge;
                }
            }
        }

        var loadedEdgeCount = nodes.Values.Sum(node => node.Edges.Count);
        var sourceShardKeys = shards
            .SelectMany(shard => shard.SourceShardKeys.Count > 0
                ? shard.SourceShardKeys
                : shard.ShardKey is null
                    ? Array.Empty<string>()
                    : new[] { shard.ShardKey })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        var graphData = new RouteGraphData
        {
            Nodes = nodes,
            ShardKey = cacheKey,
            SourceShardKeys = sourceShardKeys,
            LoadedEdgeCount = loadedEdgeCount,
            IsTruncated = shards.Any(shard => shard.IsTruncated) || loadedEdgeCount >= edgeLimit,
            SpatialBucketSizeDegrees = shards.Min(shard => shard.SpatialBucketSizeDegrees)
        };
        RouteGraphSpatialIndex.BuildSpatialBuckets(graphData);
        RouteGraphPreprocessor.TryAttachPreprocessing(graphData, _options);
        return graphData;
    }

    private static string BuildCacheKey(GraphShardRegion region, int edgeLimit, string graphVersion, string scope) =>
        string.Create(CultureInfo.InvariantCulture,
            $"route_graph:v7:{RouteGraphArtifactCodec.SchemaVersion}:ew{RouteEdgeCostModel.EdgeWeightVersion}:alt{RouteGraphPreprocessor.AltAlgorithmVersion}:{scope}:{graphVersion}:{edgeLimit}:{region.MinLon:F4}:{region.MinLat:F4}:{region.MaxLon:F4}:{region.MaxLat:F4}");

    private static void BuildSpatialBuckets(RouteGraphData graphData)
        => RouteGraphSpatialIndex.BuildSpatialBuckets(graphData);

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
            graphData.SourceShardKeys.ToArray(),
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
                    CreateGraphEdge)
            });

        var graphData = new RouteGraphData
        {
            Nodes = nodes,
            IsTruncated = snapshot.IsTruncated,
            ShardKey = snapshot.ShardKey,
            SourceShardKeys = snapshot.SourceShardKeys ?? Array.Empty<string>(),
            LoadedEdgeCount = snapshot.LoadedEdgeCount,
            SpatialBucketSizeDegrees = snapshot.SpatialBucketSizeDegrees
        };
        BuildSpatialBuckets(graphData);
        return graphData;
    }


    private sealed record RouteGraphSnapshot(
        string? ShardKey,
        string[] SourceShardKeys,
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

public sealed record GraphShardRegion(double MinLon, double MinLat, double MaxLon, double MaxLat);
