using System.Diagnostics;
using AccessCity.API.Configuration;
using AccessCity.API.Data;
using AccessCity.API.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AccessCity.API.Services;

/// <summary>
/// Orchestrates building a complete set of versioned route graph artifacts for a
/// given bounding box. Produces pre-built .acrg shards + a manifest.json so that
/// deployments can load the graph instantly without runtime PostGIS queries.
///
/// Activated via <c>Routing:RouteGraphReleaseBuildAndExit=true</c> or the
/// <c>--build-route-graph-release</c> CLI flag.
/// </summary>
public sealed class RouteGraphReleasePipeline
{
    private readonly IRouteGraphArtifactStore _artifactStore;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RouteGraphReleasePipeline> _logger;
    private readonly RoutingOptions _options;

    public RouteGraphReleasePipeline(
        IRouteGraphArtifactStore artifactStore,
        IServiceScopeFactory scopeFactory,
        IOptions<RoutingOptions> options,
        ILogger<RouteGraphReleasePipeline> logger)
    {
        _artifactStore = artifactStore;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Build all shards for the specified bounding box region, write them as
    /// versioned .acrg files, and generate a release manifest.
    /// </summary>
    public async Task<RouteGraphReleaseResult> BuildReleaseAsync(
        RouteGraphReleaseBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var shardSize = request.ShardSizeDegrees > 0
            ? request.ShardSizeDegrees
            : Math.Clamp(_options.RouteGraphShardSizeDegrees, 0.002, 0.05);
        var edgeLimitPerShard = Math.Max(100, _options.MaxRouteGraphEdges);

        var regions = PartitionBoundingBox(
            request.MinLon, request.MinLat, request.MaxLon, request.MaxLat, shardSize);

        _logger.LogInformation(
            "Starting route graph release build: {ShardCount} shards, bounding box [{MinLon},{MinLat}]→[{MaxLon},{MaxLat}], shard size {ShardSize}°",
            regions.Count, request.MinLon, request.MinLat, request.MaxLon, request.MaxLat, shardSize);

        var manifestShards = new List<RouteGraphArtifactManifestShard>(regions.Count);
        var totalNodes = 0;
        var totalEdges = 0;
        var failedShards = 0;

        foreach (var region in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var result = await BuildShardAsync(region, edgeLimitPerShard, cancellationToken);
                if (result is null)
                {
                    _logger.LogDebug(
                        "Shard [{MinLon},{MinLat}]→[{MaxLon},{MaxLat}] has no coverage, skipping",
                        region.MinLon, region.MinLat, region.MaxLon, region.MaxLat);
                    continue;
                }

                manifestShards.Add(result.ManifestShard);
                totalNodes += result.NodeCount;
                totalEdges += result.EdgeCount;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failedShards++;
                _logger.LogWarning(ex,
                    "Failed to build shard [{MinLon},{MinLat}]→[{MaxLon},{MaxLat}]",
                    region.MinLon, region.MinLat, region.MaxLon, region.MaxLat);
            }
        }

        if (manifestShards.Count == 0)
        {
            _logger.LogWarning("Route graph release build produced zero shards");
            return new RouteGraphReleaseResult(
                Success: false,
                ShardCount: 0,
                TotalNodes: 0,
                TotalEdges: 0,
                TotalPayloadBytes: 0,
                CoverageAreaKm2: 0,
                BuildDurationSeconds: stopwatch.Elapsed.TotalSeconds,
                FailedShards: failedShards,
                ReleaseVersion: "",
                ManifestPath: null);
        }

        var releaseVersion = BuildReleaseVersion();
        var coverageAreaKm2 = ComputeCoverageAreaKm2(manifestShards);

        var manifest = new RouteGraphArtifactManifest(
            SchemaVersion: RouteGraphArtifactCodec.SchemaVersion,
            EdgeCostVersion: RouteEdgeCostModel.Version,
            EdgeWeightVersion: RouteEdgeCostModel.EdgeWeightVersion,
            AltAlgorithmVersion: RouteGraphPreprocessor.AltAlgorithmVersion,
            ShardSizeDegrees: shardSize,
            SourceName: request.SourceName ?? "release-pipeline",
            CreatedAtUtc: DateTime.UtcNow,
            Shards: manifestShards.ToArray(),
            ReleaseVersion: releaseVersion,
            OsmExtractTimestamp: request.OsmExtractTimestamp,
            CoverageAreaKm2: coverageAreaKm2,
            BuildDurationSeconds: stopwatch.Elapsed.TotalSeconds);

        var writeResult = await _artifactStore.WriteManifestAsync(manifest, cancellationToken);

        _logger.LogInformation(
            "Route graph release build completed: {ShardCount} shards, {NodeCount} nodes, {EdgeCount} edges, " +
            "{PayloadBytes} bytes, {CoverageArea:F1} km², {Duration:F1}s, release={ReleaseVersion}",
            manifestShards.Count, totalNodes, totalEdges,
            manifest.TotalPayloadBytes, coverageAreaKm2,
            stopwatch.Elapsed.TotalSeconds, releaseVersion);

        return new RouteGraphReleaseResult(
            Success: true,
            ShardCount: manifestShards.Count,
            TotalNodes: totalNodes,
            TotalEdges: totalEdges,
            TotalPayloadBytes: manifest.TotalPayloadBytes,
            CoverageAreaKm2: coverageAreaKm2,
            BuildDurationSeconds: stopwatch.Elapsed.TotalSeconds,
            FailedShards: failedShards,
            ReleaseVersion: releaseVersion,
            ManifestPath: writeResult?.ArtifactPath);
    }

    /// <summary>
    /// Validate an existing release by re-reading every shard and verifying integrity.
    /// </summary>
    public async Task<RouteGraphReleaseValidationResult> ValidateReleaseAsync(
        CancellationToken cancellationToken = default)
    {
        var manifest = await _artifactStore.TryReadManifestAsync(cancellationToken);
        if (manifest is null)
        {
            return new RouteGraphReleaseValidationResult(
                Valid: false,
                ShardsChecked: 0,
                ShardsValid: 0,
                Errors: new[] { "No manifest found or manifest is incompatible." });
        }

        var errors = new List<string>();
        var valid = 0;

        foreach (var shard in manifest.Shards)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await _artifactStore.TryReadManifestShardAsync(shard, cancellationToken);
            if (result is null)
            {
                errors.Add($"Shard {shard.CacheKey} ({shard.ArtifactFileName}): failed to read or verify integrity.");
                continue;
            }

            if (!RouteGraphArtifactCodec.IsCompatible(result.Artifact))
            {
                errors.Add($"Shard {shard.CacheKey}: schema/cost/weight version mismatch.");
                continue;
            }

            var unpacked = RouteGraphArtifactCodec.Unpack(result.Artifact);
            if (!unpacked.HasCoverage)
            {
                errors.Add($"Shard {shard.CacheKey}: unpacked artifact has no coverage (0 nodes).");
                continue;
            }

            valid++;
        }

        _logger.LogInformation(
            "Route graph release validation: {Valid}/{Total} shards valid, {ErrorCount} errors",
            valid, manifest.Shards.Length, errors.Count);

        return new RouteGraphReleaseValidationResult(
            Valid: errors.Count == 0,
            ShardsChecked: manifest.Shards.Length,
            ShardsValid: valid,
            Errors: errors.ToArray());
    }

    private async Task<ShardBuildResult?> BuildShardAsync(
        GraphShardRegion region,
        int edgeLimit,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

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
            return null;
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
                fromNode.Edges[edge.ToNodeId] = graphEdge;
            }
        }

        var cacheKey = $"release:{region.MinLon:F6},{region.MinLat:F6},{region.MaxLon:F6},{region.MaxLat:F6}";
        var graphData = new RouteGraphData
        {
            Nodes = graph,
            IsTruncated = isTruncated,
            ShardKey = cacheKey,
            SourceShardKeys = new[] { cacheKey },
            LoadedEdgeCount = edges.Count,
            SpatialBucketSizeDegrees = 0.001
        };
        RouteGraphSpatialIndex.BuildSpatialBuckets(graphData);
        RouteGraphPreprocessor.TryAttachPreprocessing(graphData, _options);

        var artifact = RouteGraphArtifactCodec.Pack(graphData);
        var redisPayload = RouteGraphArtifactCodec.SerializeRedisPayload(artifact);

        var writeResult = await _artifactStore.WriteAsync(
            cacheKey, artifact, redisPayload, "release-pipeline", cancellationToken);

        if (writeResult is null)
        {
            return null;
        }

        var manifestShard = new RouteGraphArtifactManifestShard(
            CacheKey: cacheKey,
            MinLon: region.MinLon,
            MinLat: region.MinLat,
            MaxLon: region.MaxLon,
            MaxLat: region.MaxLat,
            NodeCount: graph.Count,
            EdgeCount: edges.Count,
            PayloadBytes: writeResult.PayloadBytes,
            CreatedAtUtc: writeResult.CreatedAtUtc,
            SourceType: "release-pipeline",
            ArtifactFileName: System.IO.Path.GetFileName(writeResult.ArtifactPath),
            PayloadSha256: writeResult.PayloadSha256);

        _logger.LogDebug(
            "Built shard {CacheKey}: {NodeCount} nodes, {EdgeCount} edges, {PayloadBytes} bytes",
            cacheKey, graph.Count, edges.Count, writeResult.PayloadBytes);

        return new ShardBuildResult(manifestShard, graph.Count, edges.Count);
    }

    private static IReadOnlyList<GraphShardRegion> PartitionBoundingBox(
        double minLon, double minLat, double maxLon, double maxLat, double shardSize)
    {
        var minX = (int)Math.Floor(minLon / shardSize);
        var minY = (int)Math.Floor(minLat / shardSize);
        var maxX = (int)Math.Ceiling(maxLon / shardSize) - 1;
        var maxY = (int)Math.Ceiling(maxLat / shardSize) - 1;

        var regions = new List<GraphShardRegion>();
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

    private static string BuildReleaseVersion()
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        return $"{RouteGraphArtifactCodec.SchemaVersion}.cost{RouteEdgeCostModel.Version}" +
               $".weight{RouteEdgeCostModel.EdgeWeightVersion}" +
               $".alt{RouteGraphPreprocessor.AltAlgorithmVersion}" +
               $".{timestamp}";
    }

    private static double ComputeCoverageAreaKm2(IReadOnlyList<RouteGraphArtifactManifestShard> shards)
    {
        // Approximate area using equirectangular projection
        double totalArea = 0;
        foreach (var shard in shards)
        {
            var midLat = (shard.MinLat + shard.MaxLat) / 2.0;
            var latExtent = (shard.MaxLat - shard.MinLat) * 111.32; // km per degree latitude
            var lonExtent = (shard.MaxLon - shard.MinLon) * 111.32 * Math.Cos(midLat * Math.PI / 180.0);
            totalArea += latExtent * lonExtent;
        }
        return totalArea;
    }

    private sealed record ShardBuildResult(
        RouteGraphArtifactManifestShard ManifestShard,
        int NodeCount,
        int EdgeCount);
}

public sealed record RouteGraphReleaseBuildRequest(
    double MinLon,
    double MinLat,
    double MaxLon,
    double MaxLat,
    double ShardSizeDegrees = 0,
    string? SourceName = null,
    DateTime? OsmExtractTimestamp = null);

public sealed record RouteGraphReleaseResult(
    bool Success,
    int ShardCount,
    int TotalNodes,
    int TotalEdges,
    long TotalPayloadBytes,
    double CoverageAreaKm2,
    double BuildDurationSeconds,
    int FailedShards,
    string ReleaseVersion,
    string? ManifestPath);

public sealed record RouteGraphReleaseValidationResult(
    bool Valid,
    int ShardsChecked,
    int ShardsValid,
    string[] Errors);
