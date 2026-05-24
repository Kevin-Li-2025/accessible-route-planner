using AccessCity.API.Configuration;
using AccessCity.API.Models;

namespace AccessCity.API.Services;

public static class RouteGraphProfileQualityEvaluator
{
    public static RouteGraphProfileResponse Finalize(
        RouteGraphProfileResponse response,
        RoutingOptions options)
    {
        var routes = response.Routes;
        var cacheableDistributedRoutes = routes
            .Where(route => route.WouldCacheDistributedPayload)
            .ToArray();
        var maxCacheableHotLoadMilliseconds = MaxOrZero(
            cacheableDistributedRoutes,
            route => route.HotLoadMilliseconds);
        var maxCacheableArtifactStoreReadMilliseconds = MaxOrZero(
            cacheableDistributedRoutes,
            route => route.ArtifactStoreReadMilliseconds);
        var maxCacheableArtifactUnpackMilliseconds = MaxOrZero(
            cacheableDistributedRoutes,
            route => route.ArtifactUnpackMilliseconds);
        response.MaxRedisPayloadBytes = routes.Count == 0 ? 0 : routes.Max(route => route.RedisPayloadBytes);
        response.AverageShardReferencesPerRoute = routes.Count == 0
            ? 0
            : Math.Round(routes.Average(route => route.SourceShardCount), 2);
        response.MaxSourceShardCountPerRoute = routes.Count == 0 ? 0 : routes.Max(route => route.SourceShardCount);
        response.MaxPreprocessingMilliseconds = routes.Count == 0 ? 0 : routes.Max(route => route.PreprocessingMilliseconds);
        response.MaxArtifactPackMilliseconds = routes.Count == 0 ? 0 : routes.Max(route => route.ArtifactPackMilliseconds);
        response.P95HotLoadMilliseconds = Percentile(routes.Select(route => route.HotLoadMilliseconds), 0.95);
        response.P95ArtifactUnpackMilliseconds = Percentile(
            routes.Select(route => route.ArtifactUnpackMilliseconds),
            0.95);

        var warnings = new List<string>();
        AddIf(response.SourceIsTruncated, warnings, "source graph was truncated before profiling completed");
        AddIf(routes.Any(route => route.IsTruncated), warnings, "at least one profiled route graph slice was truncated");
        AddIf(
            response.MaxRedisPayloadBytes > options.RouteGraphProfileMaxRedisPayloadBytes,
            warnings,
            $"max Redis payload {response.MaxRedisPayloadBytes} bytes exceeds {options.RouteGraphProfileMaxRedisPayloadBytes} bytes");
        AddIf(
            response.MaxArtifactBytes > options.RouteGraphProfileMaxArtifactBytes,
            warnings,
            $"max JSON artifact {response.MaxArtifactBytes} bytes exceeds {options.RouteGraphProfileMaxArtifactBytes} bytes");
        AddIf(
            response.MaxColdLoadMilliseconds > options.RouteGraphProfileMaxColdLoadMilliseconds,
            warnings,
            $"max cold graph load {response.MaxColdLoadMilliseconds:F1}ms exceeds {options.RouteGraphProfileMaxColdLoadMilliseconds:F1}ms");
        AddIf(
            maxCacheableHotLoadMilliseconds > options.RouteGraphProfileMaxHotLoadMilliseconds,
            warnings,
            $"max cacheable hot graph load {maxCacheableHotLoadMilliseconds:F1}ms exceeds {options.RouteGraphProfileMaxHotLoadMilliseconds:F1}ms");
        AddIf(
            response.MaxArtifactPackMilliseconds > options.RouteGraphProfileMaxArtifactPackMilliseconds,
            warnings,
            $"max artifact pack {response.MaxArtifactPackMilliseconds:F1}ms exceeds {options.RouteGraphProfileMaxArtifactPackMilliseconds:F1}ms");
        AddIf(
            maxCacheableArtifactStoreReadMilliseconds > options.RouteGraphProfileMaxArtifactStoreReadMilliseconds,
            warnings,
            $"max cacheable artifact store read {maxCacheableArtifactStoreReadMilliseconds:F1}ms exceeds {options.RouteGraphProfileMaxArtifactStoreReadMilliseconds:F1}ms");
        AddIf(
            maxCacheableArtifactUnpackMilliseconds > options.RouteGraphProfileMaxArtifactUnpackMilliseconds,
            warnings,
            $"max cacheable artifact unpack {maxCacheableArtifactUnpackMilliseconds:F1}ms exceeds {options.RouteGraphProfileMaxArtifactUnpackMilliseconds:F1}ms");
        AddIf(
            response.MaxSourceShardCountPerRoute > options.RouteGraphProfileMaxShardReferencesPerRoute,
            warnings,
            $"max source shards per route {response.MaxSourceShardCountPerRoute} exceeds {options.RouteGraphProfileMaxShardReferencesPerRoute}");
        var oversizedDistributedPayloads = routes.Count(route => !route.WouldCacheDistributedPayload);
        AddIf(
            oversizedDistributedPayloads > 0,
            warnings,
            $"{oversizedDistributedPayloads} route bundle(s) exceed RouteGraphMaxDistributedSnapshotBytes and will not be written to distributed cache");
        AddIf(
            options.RouteGraphAltPreprocessingEnabled
            && routes.Any(route => route.EdgeCount <= options.RouteGraphMaxAltPreprocessedNodes && !route.HasAltPreprocessing),
            warnings,
            "ALT preprocessing was expected but missing on at least one eligible route slice");
        AddIf(
            options.RouteGraphOfflineShardArtifactBuildEnabled
            && response.SourceShardCount > 0
            && options.RouteGraphOfflineShardArtifactBuildLimit <= 0
            && response.PersistedShardArtifactCount < response.SourceShardCount,
            warnings,
            $"only {response.PersistedShardArtifactCount}/{response.SourceShardCount} source shards were persisted");

        response.QualityGateWarnings = warnings;
        response.QualityGatePassed = warnings.Count == 0;
        return response;
    }

    private static double Percentile(IEnumerable<double> samples, double percentile)
    {
        var ordered = samples.OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
        {
            return 0;
        }

        var clamped = Math.Clamp(percentile, 0, 1);
        var index = (int)Math.Ceiling(clamped * ordered.Length) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }

    private static double MaxOrZero(
        IReadOnlyCollection<RouteGraphProfileRouteResult> routes,
        Func<RouteGraphProfileRouteResult, double> selector) =>
        routes.Count == 0 ? 0 : routes.Max(selector);

    private static void AddIf(bool condition, List<string> warnings, string warning)
    {
        if (condition)
        {
            warnings.Add(warning);
        }
    }
}
