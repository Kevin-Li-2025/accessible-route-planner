using System.Diagnostics;
using System.Globalization;
using AccessCity.API.Configuration;
using AccessCity.API.Models;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using OsmSharp;
using OsmSharp.Streams;
using OsmSharp.Tags;

namespace AccessCity.API.Services;

public interface IOsmRouteGraphExtractProfileService
{
    Task<RouteGraphProfileResponse> ProfileAsync(
        string filePathConfig,
        RouteGraphProfileRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class OsmRouteGraphExtractProfileService : IOsmRouteGraphExtractProfileService
{
    private readonly RoutingOptions _options;
    private readonly IRouteGraphArtifactStore _artifactStore;
    private readonly ILogger<OsmRouteGraphExtractProfileService> _logger;

    public OsmRouteGraphExtractProfileService(
        IOptions<RoutingOptions> options,
        IRouteGraphArtifactStore artifactStore,
        ILogger<OsmRouteGraphExtractProfileService> logger)
    {
        _options = options.Value;
        _artifactStore = artifactStore;
        _logger = logger;
    }

    public async Task<RouteGraphProfileResponse> ProfileAsync(
        string filePathConfig,
        RouteGraphProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        var filePath = ResolveSingleExistingFile(filePathConfig);
        var build = await BuildGraphFromExtractAsync(filePath, cancellationToken);
        var routes = ResolveRoutes(request);
        var hotReads = Math.Clamp(request.HotReadsPerRoute, 0, 5);
        var results = new List<RouteGraphProfileRouteResult>(routes.Count);
        var shardReferences = new List<string>();
        var persistedShardArtifacts = await PersistOfflineShardArtifactsAsync(filePath, build.ShardIndex, cancellationToken);

        foreach (var route in routes)
        {
            var start = new Coordinate(route.StartLng, route.StartLat);
            var end = new Coordinate(route.EndLng, route.EndLat);
            var regions = ComputeProfileShardRegions(start, end);

            var cold = Stopwatch.StartNew();
            var graphData = SliceGraph(build.ShardIndex, regions, filePath, _options.MaxRouteGraphEdges);
            var preprocessing = Stopwatch.StartNew();
            RouteGraphPreprocessor.TryAttachPreprocessing(graphData, _options);
            preprocessing.Stop();
            cold.Stop();

            var pack = Stopwatch.StartNew();
            var artifact = RouteGraphArtifactCodec.Pack(graphData);
            var redisPayload = RouteGraphArtifactCodec.SerializeRedisPayload(artifact);
            pack.Stop();
            var artifactPayload = RouteGraphArtifactCodec.SerializeJsonBytes(artifact);
            var artifactStoreWriteMilliseconds = 0.0;
            var artifactStoreReadMilliseconds = 0.0;
            RouteGraphArtifactStoreWriteResult? artifactStoreWrite = null;
            if (_artifactStore.IsEnabled && graphData.ShardKey is not null)
            {
                var artifactStoreWriteStopwatch = Stopwatch.StartNew();
                artifactStoreWrite = await _artifactStore.WriteAsync(
                    graphData.ShardKey,
                    artifact,
                    redisPayload,
                    "osm-extract-profile-bundle",
                    cancellationToken);
                artifactStoreWriteStopwatch.Stop();
                artifactStoreWriteMilliseconds = artifactStoreWriteStopwatch.Elapsed.TotalMilliseconds;

                if (artifactStoreWrite is not null)
                {
                    var artifactStoreReadStopwatch = Stopwatch.StartNew();
                    var fileArtifact = await _artifactStore.TryReadAsync(graphData.ShardKey, cancellationToken);
                    if (fileArtifact is null)
                    {
                        throw new InvalidOperationException("Packed route graph artifact could not be restored from the file artifact store.");
                    }

                    RouteGraphArtifactCodec.Unpack(fileArtifact.Artifact);
                    artifactStoreReadStopwatch.Stop();
                    artifactStoreReadMilliseconds = artifactStoreReadStopwatch.Elapsed.TotalMilliseconds;
                }
            }

            var unpack = Stopwatch.StartNew();
            if (!RouteGraphArtifactCodec.TryDeserializeRedisPayload(redisPayload, out var restoredArtifact)
                || restoredArtifact is null)
            {
                throw new InvalidOperationException("Packed route graph artifact could not be restored from the Redis payload.");
            }

            RouteGraphArtifactCodec.Unpack(restoredArtifact);
            unpack.Stop();

            var hotLoadMilliseconds = 0.0;
            for (var i = 0; i < hotReads; i++)
            {
                var hot = Stopwatch.StartNew();
                if (!RouteGraphArtifactCodec.TryDeserializeRedisPayload(redisPayload, out var hotArtifact)
                    || hotArtifact is null)
                {
                    throw new InvalidOperationException("Packed route graph artifact could not be restored from the Redis payload.");
                }

                RouteGraphArtifactCodec.Unpack(hotArtifact);
                hot.Stop();
                hotLoadMilliseconds = Math.Max(hotLoadMilliseconds, hot.Elapsed.TotalMilliseconds);
            }

            var routeSourceShards = graphData.SourceShardKeys.Count > 0
                ? graphData.SourceShardKeys
                : graphData.ShardKey is null
                    ? Array.Empty<string>()
                    : new[] { graphData.ShardKey };
            shardReferences.AddRange(routeSourceShards);

            results.Add(new RouteGraphProfileRouteResult
            {
                Name = route.Name,
                ShardKey = graphData.ShardKey,
                SourceShardCount = routeSourceShards.Count,
                NodeCount = graphData.Nodes.Count,
                EdgeCount = graphData.LoadedEdgeCount,
                IsTruncated = graphData.IsTruncated,
                HasAltPreprocessing = graphData.Preprocessing?.HasLandmarks == true,
                LandmarkCount = graphData.Preprocessing?.LandmarkNodeIds.Length ?? 0,
                AltPreprocessedNodeCount = graphData.Preprocessing?.NodeDistances.Count ?? 0,
                ArtifactBytes = artifactPayload.LongLength,
                RedisPayloadBytes = redisPayload.LongLength,
                PersistedArtifact = artifactStoreWrite is not null,
                PersistedArtifactPath = artifactStoreWrite?.ArtifactPath,
                ColdLoadMilliseconds = cold.Elapsed.TotalMilliseconds,
                HotLoadMilliseconds = hotLoadMilliseconds,
                PreprocessingMilliseconds = preprocessing.Elapsed.TotalMilliseconds,
                ArtifactPackMilliseconds = pack.Elapsed.TotalMilliseconds,
                ArtifactStoreWriteMilliseconds = artifactStoreWriteMilliseconds,
                ArtifactStoreReadMilliseconds = artifactStoreReadMilliseconds,
                ArtifactUnpackMilliseconds = unpack.Elapsed.TotalMilliseconds
            });
        }

        var uniqueShardReferences = shardReferences.Distinct(StringComparer.Ordinal).Count();
        return new RouteGraphProfileResponse
        {
            ProfiledAtUtc = DateTime.UtcNow,
            SourceType = "osm-extract-offline",
            SourceName = filePath,
            SourceBuildMilliseconds = build.BuildMilliseconds,
            SourceRecordsSeen = build.RecordsSeen,
            SourceNodeCount = build.GraphData.Nodes.Count,
            SourceEdgeCount = build.GraphData.LoadedEdgeCount,
            SourceIsTruncated = build.GraphData.IsTruncated,
            SourceShardCount = build.ShardIndex.Shards.Count,
            ArtifactSchemaVersion = RouteGraphArtifactCodec.SchemaVersion,
            EdgeCostVersion = RouteEdgeCostModel.Version,
            EdgeWeightVersion = RouteEdgeCostModel.EdgeWeightVersion,
            PreprocessingAlgorithm = $"ALT-v{RouteGraphPreprocessor.AltAlgorithmVersion}",
            RouteCount = results.Count,
            TotalShardReferences = shardReferences.Count,
            UniqueShardReferences = uniqueShardReferences,
            ShardReuseRatio = shardReferences.Count == 0
                ? 0
                : Math.Round(1.0 - (uniqueShardReferences / (double)shardReferences.Count), 4),
            TotalArtifactBytes = results.Sum(result => result.ArtifactBytes),
            MaxArtifactBytes = results.Count == 0 ? 0 : results.Max(result => result.ArtifactBytes),
            TotalRedisPayloadBytes = results.Sum(result => result.RedisPayloadBytes),
            PersistedShardArtifactCount = persistedShardArtifacts.Count,
            PersistedShardArtifactBytes = persistedShardArtifacts.Bytes,
            PersistedShardArtifactBuildMilliseconds = persistedShardArtifacts.ElapsedMilliseconds,
            MaxColdLoadMilliseconds = results.Count == 0 ? 0 : results.Max(result => result.ColdLoadMilliseconds),
            MaxHotLoadMilliseconds = results.Count == 0 ? 0 : results.Max(result => result.HotLoadMilliseconds),
            MaxArtifactStoreReadMilliseconds = results.Count == 0 ? 0 : results.Max(result => result.ArtifactStoreReadMilliseconds),
            MaxArtifactUnpackMilliseconds = results.Count == 0 ? 0 : results.Max(result => result.ArtifactUnpackMilliseconds),
            Routes = results
        };
    }

    private async Task<OfflineShardArtifactBuildResult> PersistOfflineShardArtifactsAsync(
        string filePath,
        OfflineShardIndex shardIndex,
        CancellationToken cancellationToken)
    {
        if (!_artifactStore.IsEnabled || !_options.RouteGraphOfflineShardArtifactBuildEnabled)
        {
            return new OfflineShardArtifactBuildResult(0, 0, 0);
        }

        var limit = _options.RouteGraphOfflineShardArtifactBuildLimit <= 0
            ? int.MaxValue
            : _options.RouteGraphOfflineShardArtifactBuildLimit;
        var stopwatch = Stopwatch.StartNew();
        var count = 0;
        var bytes = 0L;
        var manifestShards = new List<RouteGraphArtifactManifestShard>();

        foreach (var shardKey in shardIndex.Shards.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (count >= limit)
            {
                break;
            }

            var graphData = BuildShardGraphData(shardKey, shardIndex.Shards[shardKey]);
            if (!graphData.HasCoverage)
            {
                continue;
            }

            RouteGraphPreprocessor.TryAttachPreprocessing(graphData, _options);
            var artifact = RouteGraphArtifactCodec.Pack(graphData);
            var redisPayload = RouteGraphArtifactCodec.SerializeRedisPayload(artifact);
            var write = await _artifactStore.WriteAsync(
                shardKey,
                artifact,
                redisPayload,
                "osm-extract-source-shard",
                cancellationToken);
            if (write is null)
            {
                continue;
            }

            count++;
            bytes += write.PayloadBytes;
            if (shardIndex.ShardRegions.TryGetValue(shardKey, out var region))
            {
                manifestShards.Add(new RouteGraphArtifactManifestShard(
                    shardKey,
                    region.MinLon,
                    region.MinLat,
                    region.MaxLon,
                    region.MaxLat,
                    graphData.Nodes.Count,
                    graphData.LoadedEdgeCount,
                    write.PayloadBytes,
                    write.CreatedAtUtc,
                    "osm-extract-source-shard",
                    Path.GetFileName(write.ArtifactPath)));
            }
        }

        if (manifestShards.Count > 0)
        {
            await _artifactStore.WriteManifestAsync(
                new RouteGraphArtifactManifest(
                    RouteGraphArtifactCodec.SchemaVersion,
                    RouteEdgeCostModel.Version,
                    RouteEdgeCostModel.EdgeWeightVersion,
                    RouteGraphPreprocessor.AltAlgorithmVersion,
                    Math.Clamp(_options.RouteGraphShardSizeDegrees, 0.002, 0.05),
                    Path.GetFileName(filePath),
                    DateTime.UtcNow,
                    manifestShards
                        .OrderBy(shard => shard.CacheKey, StringComparer.Ordinal)
                        .ToArray()),
                cancellationToken);
        }

        stopwatch.Stop();
        _logger.LogInformation(
            "Persisted {ArtifactCount} offline route graph shard artifacts ({ArtifactBytes} bytes) in {ElapsedMilliseconds}ms",
            count,
            bytes,
            stopwatch.Elapsed.TotalMilliseconds);
        return new OfflineShardArtifactBuildResult(count, bytes, stopwatch.Elapsed.TotalMilliseconds);
    }

    private async Task<OsmRouteGraphBuildResult> BuildGraphFromExtractAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            var stopwatch = Stopwatch.StartNew();
            var recordsSeen = 0L;
            var nodeCache = new Dictionary<long, Coordinate>();
            var graph = new Dictionary<long, GraphNode>();
            var edgeLimit = Math.Max(100, _options.MaxRouteGraphEdges);
            var edgeCount = 0;
            var isTruncated = false;

            _logger.LogInformation("Profiling OSM route graph extract pass 1 (nodes): {FilePath}", filePath);
            using (var stream = File.OpenRead(filePath))
            {
                foreach (var osmGeo in CreateSource(stream, filePath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    recordsSeen++;

                    if (osmGeo is Node node && node.Id.HasValue && node.Longitude.HasValue && node.Latitude.HasValue)
                    {
                        nodeCache[node.Id.Value] = new Coordinate(node.Longitude.Value, node.Latitude.Value);
                    }
                }
            }

            _logger.LogInformation("Profiling OSM route graph extract pass 2 (ways): {FilePath}", filePath);
            using (var stream = File.OpenRead(filePath))
            {
                foreach (var osmGeo in CreateSource(stream, filePath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    recordsSeen++;

                    if (osmGeo is not Way way || !IsWalkable(way))
                    {
                        continue;
                    }

                    AddWalkableWay(way, nodeCache, graph, ref edgeCount, ref isTruncated, edgeLimit);
                    if (isTruncated)
                    {
                        break;
                    }
                }
            }

            nodeCache.Clear();
            var graphData = new RouteGraphData
            {
                Nodes = graph,
                LoadedEdgeCount = graph.Values.Sum(node => node.Edges.Count),
                IsTruncated = isTruncated,
                ShardKey = BuildOfflineSourceKey(filePath),
                SourceShardKeys = new[] { BuildOfflineSourceKey(filePath) },
                SpatialBucketSizeDegrees = 0.001
            };
            RouteGraphSpatialIndex.BuildSpatialBuckets(graphData);
            var shardIndex = BuildShardIndex(graphData, filePath);
            stopwatch.Stop();

            _logger.LogInformation(
                "Built offline OSM route graph from {FilePath}: {NodeCount} nodes, {EdgeCount} edges, {ShardCount} shards, truncated={IsTruncated}, durationMs={DurationMs}",
                filePath,
                graphData.Nodes.Count,
                graphData.LoadedEdgeCount,
                shardIndex.Shards.Count,
                graphData.IsTruncated,
                stopwatch.Elapsed.TotalMilliseconds);

            return new OsmRouteGraphBuildResult(graphData, shardIndex, recordsSeen, stopwatch.Elapsed.TotalMilliseconds);
        }, cancellationToken);
    }

    private List<RouteGraphProfileRouteRequest> ResolveRoutes(RouteGraphProfileRequest request)
    {
        var routes = request.Routes.Count > 0
            ? request.Routes
            : _options.RouteGraphWarmupRoutes.Select(route => new RouteGraphProfileRouteRequest
            {
                Name = route.Name,
                StartLat = route.StartLat,
                StartLng = route.StartLng,
                EndLat = route.EndLat,
                EndLng = route.EndLng
            }).ToList();

        if (routes.Count == 0)
        {
            throw new InvalidOperationException("At least one profile route or Routing:RouteGraphWarmupRoutes entry is required.");
        }

        return routes;
    }

    private RouteGraphData SliceGraph(
        OfflineShardIndex shardIndex,
        IReadOnlyList<OfflineGraphShardRegion> regions,
        string filePath,
        int edgeLimit)
    {
        var nodes = new Dictionary<long, GraphNode>();
        var loadedEdges = 0;
        var maxEdges = Math.Max(100, edgeLimit);
        var isTruncated = false;
        var sourceShardKeys = regions
            .SelectMany(region => ResolveShardKeysForRegion(shardIndex, region))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        foreach (var shardKey in sourceShardKeys)
        {
            foreach (var edgeRef in shardIndex.Shards[shardKey])
            {
                var sliceFrom = GetOrAddNode(nodes, edgeRef.FromNode);
                GetOrAddNode(nodes, edgeRef.ToNode);
                sliceFrom.Edges[edgeRef.Edge.TargetNodeId] = CloneEdge(edgeRef.Edge);
                loadedEdges++;

                if (loadedEdges >= maxEdges)
                {
                    isTruncated = true;
                    break;
                }
            }

            if (isTruncated && loadedEdges >= maxEdges)
            {
                break;
            }
        }

        var graphData = new RouteGraphData
        {
            Nodes = nodes,
            LoadedEdgeCount = nodes.Values.Sum(node => node.Edges.Count),
            IsTruncated = isTruncated,
            ShardKey = BuildOfflineBundleKey(filePath, regions, edgeLimit),
            SourceShardKeys = sourceShardKeys,
            SpatialBucketSizeDegrees = 0.001
        };
        RouteGraphSpatialIndex.BuildSpatialBuckets(graphData);
        return graphData;
    }

    private static RouteGraphData BuildShardGraphData(
        string shardKey,
        IReadOnlyList<OfflineEdgeRef> edgeRefs)
    {
        var nodes = new Dictionary<long, GraphNode>();
        foreach (var edgeRef in edgeRefs)
        {
            var sliceFrom = GetOrAddNode(nodes, edgeRef.FromNode);
            GetOrAddNode(nodes, edgeRef.ToNode);
            sliceFrom.Edges[edgeRef.Edge.TargetNodeId] = CloneEdge(edgeRef.Edge);
        }

        var graphData = new RouteGraphData
        {
            Nodes = nodes,
            LoadedEdgeCount = nodes.Values.Sum(node => node.Edges.Count),
            IsTruncated = false,
            ShardKey = shardKey,
            SourceShardKeys = new[] { shardKey },
            SpatialBucketSizeDegrees = 0.001
        };
        RouteGraphSpatialIndex.BuildSpatialBuckets(graphData);
        return graphData;
    }

    private OfflineShardIndex BuildShardIndex(RouteGraphData graphData, string filePath)
    {
        var shards = new Dictionary<string, List<OfflineEdgeRef>>(StringComparer.Ordinal);
        var shardRegions = new Dictionary<string, OfflineGraphShardRegion>(StringComparer.Ordinal);
        foreach (var fromNode in graphData.Nodes.Values)
        {
            foreach (var edge in fromNode.Edges.Values)
            {
                if (!graphData.Nodes.TryGetValue(edge.TargetNodeId, out var toNode))
                {
                    continue;
                }

                var midpoint = new Coordinate(
                    (fromNode.Location.X + toNode.Location.X) / 2.0,
                    (fromNode.Location.Y + toNode.Location.Y) / 2.0);
                var region = ComputeShardRegionForCoordinate(midpoint);
                var shardKey = BuildOfflineShardKey(filePath, region);
                shardRegions.TryAdd(shardKey, region);
                if (!shards.TryGetValue(shardKey, out var edges))
                {
                    edges = new List<OfflineEdgeRef>();
                    shards[shardKey] = edges;
                }

                edges.Add(new OfflineEdgeRef(fromNode, toNode, edge));
            }
        }

        return new OfflineShardIndex(shards, shardRegions);
    }

    private static IEnumerable<string> ResolveShardKeysForRegion(
        OfflineShardIndex shardIndex,
        OfflineGraphShardRegion region)
    {
        foreach (var (shardKey, shardRegion) in shardIndex.ShardRegions)
        {
            if (Overlaps(region, shardRegion))
            {
                yield return shardKey;
            }
        }
    }

    private static bool Overlaps(OfflineGraphShardRegion a, OfflineGraphShardRegion b) =>
        a.MinLon < b.MaxLon
        && a.MaxLon > b.MinLon
        && a.MinLat < b.MaxLat
        && a.MaxLat > b.MinLat;

    private OfflineGraphShardRegion ComputeShardRegionForCoordinate(Coordinate coordinate)
    {
        var shardSize = Math.Clamp(_options.RouteGraphShardSizeDegrees, 0.002, 0.05);
        var x = Math.Floor(coordinate.X / shardSize);
        var y = Math.Floor(coordinate.Y / shardSize);
        return new OfflineGraphShardRegion(
            x * shardSize,
            y * shardSize,
            (x + 1) * shardSize,
            (y + 1) * shardSize);
    }

    private IReadOnlyList<OfflineGraphShardRegion> ComputeProfileShardRegions(Coordinate start, Coordinate end)
    {
        var region = ComputeShardRegion(start, end);
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

        var regions = new List<OfflineGraphShardRegion>(shardCount);
        for (var x = minX; x <= maxX; x++)
        {
            for (var y = minY; y <= maxY; y++)
            {
                regions.Add(new OfflineGraphShardRegion(
                    x * shardSize,
                    y * shardSize,
                    (x + 1) * shardSize,
                    (y + 1) * shardSize));
            }
        }

        return regions;
    }

    private OfflineGraphShardRegion ComputeShardRegion(Coordinate start, Coordinate end)
    {
        var padding = ComputePaddingDegrees(start, end);
        var minLon = Math.Min(start.X, end.X) - padding;
        var maxLon = Math.Max(start.X, end.X) + padding;
        var minLat = Math.Min(start.Y, end.Y) - padding;
        var maxLat = Math.Max(start.Y, end.Y) + padding;
        var shardSize = Math.Clamp(_options.RouteGraphShardSizeDegrees, 0.002, 0.05);

        return new OfflineGraphShardRegion(
            Math.Floor(minLon / shardSize) * shardSize,
            Math.Floor(minLat / shardSize) * shardSize,
            Math.Ceiling(maxLon / shardSize) * shardSize,
            Math.Ceiling(maxLat / shardSize) * shardSize);
    }

    private static double ComputePaddingDegrees(Coordinate start, Coordinate end)
    {
        var latitudeDelta = Math.Abs(start.Y - end.Y);
        var longitudeDelta = Math.Abs(start.X - end.X);
        return Math.Max(0.01, Math.Max(latitudeDelta, longitudeDelta) * 0.35);
    }

    private static GraphNode GetOrAddNode(Dictionary<long, GraphNode> nodes, GraphNode source)
    {
        if (nodes.TryGetValue(source.Id, out var existing))
        {
            return existing;
        }

        var node = new GraphNode
        {
            Id = source.Id,
            Location = source.Location
        };
        nodes[source.Id] = node;
        return node;
    }

    private static GraphEdge CloneEdge(GraphEdge edge) => new()
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
        EdgeWeightVersion = edge.EdgeWeightVersion,
        StandardTraversalSeconds = edge.StandardTraversalSeconds,
        WheelchairTraversalSeconds = edge.WheelchairTraversalSeconds,
        StrollerTraversalSeconds = edge.StrollerTraversalSeconds,
        Geometry = edge.Geometry?.ToArray()
    };

    private static void AddWalkableWay(
        Way way,
        IReadOnlyDictionary<long, Coordinate> nodeCache,
        Dictionary<long, GraphNode> graph,
        ref int edgeCount,
        ref bool isTruncated,
        int edgeLimit)
    {
        if (way.Nodes == null || way.Nodes.Length < 2)
        {
            return;
        }

        var validNodes = new List<(long Id, Coordinate Location)>(way.Nodes.Length);
        foreach (var nodeId in way.Nodes)
        {
            if (nodeCache.TryGetValue(nodeId, out var coordinate))
            {
                validNodes.Add((nodeId, coordinate));
            }
        }

        if (validNodes.Count < 2)
        {
            return;
        }

        var tags = ToDictionary(way.Tags);
        var bidirectional = IsBidirectionalForWalking(tags);
        for (var i = 0; i < validNodes.Count - 1; i++)
        {
            var from = validNodes[i];
            var to = validNodes[i + 1];
            if (AddGraphEdge(way, tags, graph, from, to))
            {
                edgeCount++;
            }

            if (bidirectional)
            {
                if (AddGraphEdge(way, tags, graph, to, from))
                {
                    edgeCount++;
                }
            }

            if (edgeCount >= edgeLimit)
            {
                isTruncated = true;
                return;
            }
        }
    }

    private static bool AddGraphEdge(
        Way way,
        IReadOnlyDictionary<string, string> tags,
        Dictionary<long, GraphNode> graph,
        (long Id, Coordinate Location) from,
        (long Id, Coordinate Location) to)
    {
        var fromNode = GetOrAddNode(graph, from.Id, from.Location);
        GetOrAddNode(graph, to.Id, to.Location);
        var inserted = !fromNode.Edges.ContainsKey(to.Id);
        fromNode.Edges[to.Id] = CreateGraphEdge(tags, from.Location, to.Location, to.Id);
        return inserted;
    }

    private static GraphNode GetOrAddNode(Dictionary<long, GraphNode> nodes, long id, Coordinate location)
    {
        if (nodes.TryGetValue(id, out var existing))
        {
            return existing;
        }

        var node = new GraphNode
        {
            Id = id,
            Location = location
        };
        nodes[id] = node;
        return node;
    }

    private static GraphEdge CreateGraphEdge(
        IReadOnlyDictionary<string, string> tags,
        Coordinate from,
        Coordinate to,
        long targetNodeId)
    {
        var surface = GetFirstTag(
            tags,
            "surface",
            "sidewalk:surface",
            "sidewalk:left:surface",
            "sidewalk:right:surface",
            "left:surface",
            "right:surface") ?? "unknown";
        var smoothness = GetFirstTag(
            tags,
            "smoothness",
            "sidewalk:smoothness",
            "sidewalk:left:smoothness",
            "sidewalk:right:smoothness",
            "left:smoothness",
            "right:smoothness");
        var lit = tags.GetValueOrDefault("lit");
        var highway = tags.GetValueOrDefault("highway");
        var incline = tags.GetValueOrDefault("incline");
        var barrier = tags.GetValueOrDefault("barrier");
        var width = ParseMetres(GetFirstTag(
            tags,
            "width",
            "sidewalk:width",
            "sidewalk:left:width",
            "sidewalk:right:width",
            "left:width",
            "right:width"));
        var kerbHeight = ParseKerbHeight(tags);
        var wheelchair = tags.GetValueOrDefault("wheelchair");
        var access = BuildAccessDescriptor(tags);
        var distanceMetres = RiskScoringService.HaversineDistance(from.Y, from.X, to.Y, to.X);
        var hasStairs = string.Equals(highway, "steps", StringComparison.OrdinalIgnoreCase);
        var hasBarrier = IsBlockingBarrier(tags, kerbHeight);
        var isSteep = IsSteep(incline);
        var costProfile = RouteEdgeCostModel.Compute(
            distanceMetres,
            surface,
            smoothness,
            hasStairs,
            hasBarrier,
            kerbHeight,
            width,
            isSteep,
            access);

        var edge = new GraphEdge
        {
            TargetNodeId = targetNodeId,
            DistanceMetres = distanceMetres,
            BaseSafetyCost = ComputeBaseSafetyCost(surface, smoothness, lit, highway, incline, barrier, kerbHeight, width, wheelchair),
            SurfaceType = surface,
            HasStairs = hasStairs,
            HasCrossing = HasCrossing(tags),
            LightingQuality = lit?.ToLowerInvariant() switch
            {
                "yes" => 0.95,
                "limited" => 0.55,
                "no" => 0.1,
                _ => 0.45
            },
            IsSteep = isSteep,
            IsUnderConstruction = IsUnderConstruction(tags),
            KerbHeight = kerbHeight,
            Smoothness = smoothness,
            WidthMetres = width,
            HasTactilePaving = IsYes(GetFirstTag(tags, "tactile_paving", "sidewalk:tactile_paving")),
            HasBarrier = hasBarrier,
            Access = access,
            AccessibilityCostVersion = costProfile.Version,
            StandardAccessibilityPenaltySeconds = costProfile.StandardAccessibilityPenaltySeconds,
            WheelchairAccessibilityPenaltySeconds = costProfile.WheelchairAccessibilityPenaltySeconds,
            StrollerAccessibilityPenaltySeconds = costProfile.StrollerAccessibilityPenaltySeconds,
            AccessibilityDataQuality = costProfile.AccessibilityDataQuality,
            Geometry = new[] { from, to }
        };
        RouteEdgeCostModel.PopulateTraversalWeights(edge);
        return edge;
    }

    private static OsmStreamSource CreateSource(Stream stream, string filePath) =>
        filePath.EndsWith(".pbf", StringComparison.OrdinalIgnoreCase)
            ? new PBFOsmStreamSource(stream)
            : new XmlOsmStreamSource(stream);

    private static string ResolveSingleExistingFile(string filePathConfig)
    {
        var configured = filePathConfig.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var candidate in configured)
        {
            var fullPath = Path.GetFullPath(candidate);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        throw new FileNotFoundException($"No OSM import files were found. Configured paths: {filePathConfig}");
    }

    private static string BuildOfflineSourceKey(string filePath) =>
        $"osm-extract:{Path.GetFileName(filePath)}:{RouteGraphArtifactCodec.SchemaVersion}:ew{RouteEdgeCostModel.EdgeWeightVersion}:alt{RouteGraphPreprocessor.AltAlgorithmVersion}";

    private static string BuildOfflineShardKey(string filePath, OfflineGraphShardRegion region) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{BuildOfflineSourceKey(filePath)}:cell:{region.MinLon:F4}:{region.MinLat:F4}:{region.MaxLon:F4}:{region.MaxLat:F4}");

    private static string BuildOfflineBundleKey(
        string filePath,
        IReadOnlyList<OfflineGraphShardRegion> regions,
        int edgeLimit)
    {
        var minLon = regions.Min(region => region.MinLon);
        var minLat = regions.Min(region => region.MinLat);
        var maxLon = regions.Max(region => region.MaxLon);
        var maxLat = regions.Max(region => region.MaxLat);
        return string.Create(CultureInfo.InvariantCulture,
            $"{BuildOfflineSourceKey(filePath)}:bundle{regions.Count}:{edgeLimit}:{minLon:F4}:{minLat:F4}:{maxLon:F4}:{maxLat:F4}");
    }

    private static bool IsWalkable(Way way)
    {
        var tags = ToDictionary(way.Tags);
        var highway = tags.GetValueOrDefault("highway");
        if (string.IsNullOrWhiteSpace(highway) || ExcludedHighways.Contains(highway))
        {
            return false;
        }

        if (tags.TryGetValue("foot", out var foot) &&
            string.Equals(foot, "no", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (tags.TryGetValue("access", out var access) &&
            (string.Equals(access, "no", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(access, "private", StringComparison.OrdinalIgnoreCase)) &&
            !string.Equals(tags.GetValueOrDefault("foot"), "yes", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var barrier = tags.GetValueOrDefault("barrier");
        if (!string.IsNullOrWhiteSpace(barrier) &&
            (string.Equals(barrier, "wall", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(barrier, "fence", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return WalkableHighways.Contains(highway)
            || string.Equals(tags.GetValueOrDefault("foot"), "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(tags.GetValueOrDefault("foot"), "designated", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBidirectionalForWalking(IReadOnlyDictionary<string, string> tags)
    {
        if (string.Equals(tags.GetValueOrDefault("oneway:foot"), "yes", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(tags.GetValueOrDefault("foot:backward"), "no", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static string? GetFirstTag(IReadOnlyDictionary<string, string> tags, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (tags.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string? BuildAccessDescriptor(IReadOnlyDictionary<string, string> tags)
    {
        var parts = new List<string>();
        AddAccessPart(parts, tags, "access");
        AddAccessPart(parts, tags, "foot");
        AddAccessPart(parts, tags, "wheelchair");
        return parts.Count == 0 ? null : string.Join(";", parts);
    }

    private static void AddAccessPart(List<string> parts, IReadOnlyDictionary<string, string> tags, string key)
    {
        if (tags.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            parts.Add($"{key}={value.Trim().ToLowerInvariant()}");
        }
    }

    private static double? ParseMetres(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var normalized = raw.Trim().ToLowerInvariant()
            .Replace("meters", "", StringComparison.Ordinal)
            .Replace("metres", "", StringComparison.Ordinal)
            .Replace("meter", "", StringComparison.Ordinal)
            .Replace("metre", "", StringComparison.Ordinal)
            .Replace("m", "", StringComparison.Ordinal)
            .Trim();

        if (normalized.Contains(';', StringComparison.Ordinal))
        {
            normalized = normalized.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        }

        if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var metres) && metres >= 0)
        {
            return metres;
        }

        return null;
    }

    private static double ParseKerbHeight(IReadOnlyDictionary<string, string> tags)
    {
        var explicitHeight = ParseMetres(GetFirstTag(
            tags,
            "kerb:height",
            "sidewalk:kerb:height",
            "sidewalk:left:kerb:height",
            "sidewalk:right:kerb:height",
            "sloped_curb:height"));
        if (explicitHeight.HasValue)
        {
            return explicitHeight.Value;
        }

        var kerb = GetFirstTag(tags, "kerb", "sidewalk:kerb", "sidewalk:left:kerb", "sidewalk:right:kerb");
        if (!string.IsNullOrWhiteSpace(kerb))
        {
            return kerb.ToLowerInvariant() switch
            {
                "flush" or "lowered" or "no" => 0.0,
                "rolled" => 0.03,
                "raised" => 0.10,
                _ => 0.05
            };
        }

        var slopedCurb = GetFirstTag(tags, "sloped_curb", "sidewalk:sloped_curb");
        if (!string.IsNullOrWhiteSpace(slopedCurb))
        {
            return IsYes(slopedCurb) ? 0.02 : 0.10;
        }

        return string.Equals(tags.GetValueOrDefault("barrier"), "kerb", StringComparison.OrdinalIgnoreCase)
            ? 0.10
            : 0.0;
    }

    private static bool IsBlockingBarrier(IReadOnlyDictionary<string, string> tags, double kerbHeight)
    {
        var barrier = tags.GetValueOrDefault("barrier");
        if (string.IsNullOrWhiteSpace(barrier))
        {
            return false;
        }

        if (string.Equals(barrier, "kerb", StringComparison.OrdinalIgnoreCase))
        {
            return kerbHeight > 0.05;
        }

        return barrier.ToLowerInvariant() switch
        {
            "wall" or "fence" or "gate" or "stile" or "turnstile" or "cycle_barrier" or "block" or "chain" => true,
            _ => false
        };
    }

    private static bool HasCrossing(IReadOnlyDictionary<string, string> tags) =>
        string.Equals(tags.GetValueOrDefault("highway"), "crossing", StringComparison.OrdinalIgnoreCase)
        || string.Equals(tags.GetValueOrDefault("footway"), "crossing", StringComparison.OrdinalIgnoreCase)
        || tags.ContainsKey("crossing");

    private static bool IsUnderConstruction(IReadOnlyDictionary<string, string> tags) =>
        string.Equals(tags.GetValueOrDefault("highway"), "construction", StringComparison.OrdinalIgnoreCase)
        || tags.ContainsKey("construction")
        || string.Equals(tags.GetValueOrDefault("access"), "no", StringComparison.OrdinalIgnoreCase);

    private static bool IsYes(string? value) =>
        string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase);

    private static double ComputeBaseSafetyCost(
        string surface,
        string? smoothness,
        string? lit,
        string? highway,
        string? incline,
        string? barrier,
        double kerbHeight,
        double? widthMetres,
        string? wheelchair)
    {
        var score = surface.ToLowerInvariant() switch
        {
            "asphalt" => 0.08,
            "paved" => 0.1,
            "paving_stones" => 0.14,
            "concrete" => 0.1,
            "unknown" => 0.22,
            "cobblestone" => 0.35,
            "sett" => 0.35,
            "gravel" => 0.4,
            "unpaved" => 0.45,
            "sand" or "dirt" or "earth" or "grass" => 0.5,
            _ => 0.2
        };

        if (string.IsNullOrWhiteSpace(smoothness))
        {
            score += 0.03;
        }

        score += smoothness?.ToLowerInvariant() switch
        {
            "excellent" or "good" => -0.02,
            "intermediate" => 0.02,
            "bad" => 0.15,
            "very_bad" => 0.25,
            "horrible" or "very_horrible" or "impassable" => 0.35,
            _ => 0
        };

        if (string.Equals(lit, "no", StringComparison.OrdinalIgnoreCase))
        {
            score += 0.12;
        }

        if (string.Equals(highway, "steps", StringComparison.OrdinalIgnoreCase))
        {
            score += 0.25;
        }

        if (IsSteep(incline))
        {
            score += 0.15;
        }

        if (!string.IsNullOrWhiteSpace(barrier))
        {
            score += 0.2;
        }

        if (kerbHeight > 0.03)
        {
            score += Math.Min(kerbHeight * 4.0, 0.25);
        }

        if (widthMetres.HasValue && widthMetres < 0.9)
        {
            score += 0.25;
        }
        else if (!widthMetres.HasValue && IsPedestrianInfrastructure(highway))
        {
            score += 0.06;
        }

        if (string.Equals(wheelchair, "no", StringComparison.OrdinalIgnoreCase))
        {
            score += 0.6;
        }
        else if (string.Equals(wheelchair, "limited", StringComparison.OrdinalIgnoreCase))
        {
            score += 0.2;
        }

        return Math.Clamp(score, 0.01, 0.95);
    }

    private static bool IsPedestrianInfrastructure(string? highway) =>
        !string.IsNullOrWhiteSpace(highway) && WalkableHighways.Contains(highway);

    private static bool IsSteep(string? incline)
    {
        if (string.IsNullOrWhiteSpace(incline))
        {
            return false;
        }

        var trimmed = incline.Trim().TrimEnd('%');
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var percentage))
        {
            return Math.Abs(percentage) >= 8.0;
        }

        return string.Equals(trimmed, "up", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "down", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "steep", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ToDictionary(TagsCollectionBase? tags) =>
        tags is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : tags.ToDictionary(tag => tag.Key, tag => tag.Value, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> WalkableHighways = new(StringComparer.OrdinalIgnoreCase)
    {
        "footway", "path", "pedestrian", "living_street", "service", "residential",
        "unclassified", "tertiary", "secondary", "primary", "track", "steps", "crossing"
    };

    private static readonly HashSet<string> ExcludedHighways = new(StringComparer.OrdinalIgnoreCase)
    {
        "motorway", "motorway_link", "trunk", "trunk_link", "proposed", "construction"
    };

    private readonly record struct OfflineGraphShardRegion(double MinLon, double MinLat, double MaxLon, double MaxLat);
    private readonly record struct OfflineEdgeRef(GraphNode FromNode, GraphNode ToNode, GraphEdge Edge);
    private sealed record OfflineShardIndex(
        IReadOnlyDictionary<string, List<OfflineEdgeRef>> Shards,
        IReadOnlyDictionary<string, OfflineGraphShardRegion> ShardRegions);
    private readonly record struct OfflineShardArtifactBuildResult(
        int Count,
        long Bytes,
        double ElapsedMilliseconds);
    private sealed record OsmRouteGraphBuildResult(
        RouteGraphData GraphData,
        OfflineShardIndex ShardIndex,
        long RecordsSeen,
        double BuildMilliseconds);
}
