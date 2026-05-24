using System.Diagnostics;
using AccessCity.API.Configuration;
using AccessCity.API.Models;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;

namespace AccessCity.API.Services;

public interface IRouteGraphProfileService
{
    Task<RouteGraphProfileResponse> ProfileAsync(
        RouteGraphProfileRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class RouteGraphProfileService : IRouteGraphProfileService
{
    private readonly IRouteGraphRepository _routeGraphRepository;
    private readonly RoutingOptions _options;

    public RouteGraphProfileService(
        IRouteGraphRepository routeGraphRepository,
        IOptions<RoutingOptions> options)
    {
        _routeGraphRepository = routeGraphRepository;
        _options = options.Value;
    }

    public async Task<RouteGraphProfileResponse> ProfileAsync(
        RouteGraphProfileRequest request,
        CancellationToken cancellationToken = default)
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

        var hotReads = Math.Clamp(request.HotReadsPerRoute, 0, 5);
        var results = new List<RouteGraphProfileRouteResult>(routes.Count);
        var shardReferences = new List<string>();

        foreach (var route in routes)
        {
            var start = new Coordinate(route.StartLng, route.StartLat);
            var end = new Coordinate(route.EndLng, route.EndLat);

            var coldLoad = Stopwatch.StartNew();
            var graphData = await _routeGraphRepository.LoadGraphAsync(start, end, cancellationToken);
            coldLoad.Stop();

            var hotLoadMilliseconds = 0.0;
            for (var i = 0; i < hotReads; i++)
            {
                var hotLoad = Stopwatch.StartNew();
                await _routeGraphRepository.LoadGraphAsync(start, end, cancellationToken);
                hotLoad.Stop();
                hotLoadMilliseconds = Math.Max(hotLoadMilliseconds, hotLoad.Elapsed.TotalMilliseconds);
            }

            var pack = Stopwatch.StartNew();
            var artifact = RouteGraphArtifactCodec.Pack(graphData);
            var redisPayload = RouteGraphArtifactCodec.SerializeRedisPayload(artifact);
            pack.Stop();
            var routeBundleCacheable = WouldCacheDistributedPayload(redisPayload.LongLength);
            var artifactPayload = RouteGraphArtifactCodec.SerializeJsonBytes(artifact);

            var unpack = Stopwatch.StartNew();
            if (!RouteGraphArtifactCodec.TryDeserializeRedisPayload(redisPayload, out var restoredArtifact)
                || restoredArtifact is null)
            {
                throw new InvalidOperationException("Packed route graph artifact could not be restored from the Redis payload.");
            }

            RouteGraphArtifactCodec.Unpack(restoredArtifact);
            unpack.Stop();

            var sourceShards = graphData.SourceShardKeys.Count > 0
                ? graphData.SourceShardKeys
                : graphData.ShardKey is null
                    ? Array.Empty<string>()
                    : new[] { graphData.ShardKey };
            shardReferences.AddRange(sourceShards);

            results.Add(new RouteGraphProfileRouteResult
            {
                Name = route.Name,
                ShardKey = graphData.ShardKey,
                SourceShardCount = sourceShards.Count,
                NodeCount = graphData.Nodes.Count,
                EdgeCount = graphData.LoadedEdgeCount,
                IsTruncated = graphData.IsTruncated,
                WouldCacheDistributedPayload = routeBundleCacheable,
                HasAltPreprocessing = graphData.Preprocessing?.HasLandmarks == true,
                LandmarkCount = graphData.Preprocessing?.LandmarkNodeIds.Length ?? 0,
                AltPreprocessedNodeCount = graphData.Preprocessing?.NodeDistances.Count ?? 0,
                ArtifactBytes = artifactPayload.LongLength,
                RedisPayloadBytes = redisPayload.LongLength,
                ColdLoadMilliseconds = coldLoad.Elapsed.TotalMilliseconds,
                HotLoadMilliseconds = hotLoadMilliseconds,
                ArtifactPackMilliseconds = pack.Elapsed.TotalMilliseconds,
                ArtifactUnpackMilliseconds = unpack.Elapsed.TotalMilliseconds
            });
        }

        var uniqueShardReferences = shardReferences.Distinct(StringComparer.Ordinal).Count();
        var response = new RouteGraphProfileResponse
        {
            ProfiledAtUtc = DateTime.UtcNow,
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
            MaxColdLoadMilliseconds = results.Count == 0 ? 0 : results.Max(result => result.ColdLoadMilliseconds),
            MaxHotLoadMilliseconds = results.Count == 0 ? 0 : results.Max(result => result.HotLoadMilliseconds),
            MaxArtifactUnpackMilliseconds = results.Count == 0 ? 0 : results.Max(result => result.ArtifactUnpackMilliseconds),
            Routes = results
        };

        return RouteGraphProfileQualityEvaluator.Finalize(response, _options);
    }

    private bool WouldCacheDistributedPayload(long payloadBytes) =>
        _options.RouteGraphMaxDistributedSnapshotBytes <= 0
        || payloadBytes <= _options.RouteGraphMaxDistributedSnapshotBytes;
}
