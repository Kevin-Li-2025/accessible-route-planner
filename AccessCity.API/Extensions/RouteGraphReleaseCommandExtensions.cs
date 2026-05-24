using AccessCity.API.Configuration;
using AccessCity.API.Services;
using Microsoft.Extensions.Options;

namespace AccessCity.API.Extensions;

public static class RouteGraphReleaseCommandExtensions
{
    public static async Task RunRouteGraphReleaseCommandAsync(
        this WebApplication app,
        bool buildRelease,
        bool validateRelease)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("RouteGraphRelease");
        var options = services.GetRequiredService<IOptions<RoutingOptions>>().Value;
        var pipeline = services.GetRequiredService<RouteGraphReleasePipeline>();

        if (buildRelease)
        {
            var request = BuildRequest(options);
            logger.LogInformation(
                "Building route graph release for bbox [{MinLon},{MinLat}] -> [{MaxLon},{MaxLat}]",
                request.MinLon, request.MinLat, request.MaxLon, request.MaxLat);

            var result = await pipeline.BuildReleaseAsync(request);
            if (!result.Success)
            {
                throw new InvalidOperationException("Route graph release build produced no usable shards.");
            }

            logger.LogInformation(
                "Built route graph release {ReleaseVersion}: {ShardCount} shards, {NodeCount} nodes, {EdgeCount} edges, {PayloadBytes} bytes.",
                result.ReleaseVersion,
                result.ShardCount,
                result.TotalNodes,
                result.TotalEdges,
                result.TotalPayloadBytes);
        }

        if (validateRelease)
        {
            var validation = await pipeline.ValidateReleaseAsync();
            if (!validation.Valid)
            {
                throw new InvalidOperationException(
                    "Route graph release validation failed: " + string.Join("; ", validation.Errors));
            }

            logger.LogInformation(
                "Validated route graph release: {ValidShardCount}/{ShardCount} shards readable and compatible.",
                validation.ShardsValid,
                validation.ShardsChecked);
        }
    }

    private static RouteGraphReleaseBuildRequest BuildRequest(RoutingOptions options)
    {
        if (options.RouteGraphReleaseMinLon.HasValue
            && options.RouteGraphReleaseMinLat.HasValue
            && options.RouteGraphReleaseMaxLon.HasValue
            && options.RouteGraphReleaseMaxLat.HasValue)
        {
            return new RouteGraphReleaseBuildRequest(
                options.RouteGraphReleaseMinLon.Value,
                options.RouteGraphReleaseMinLat.Value,
                options.RouteGraphReleaseMaxLon.Value,
                options.RouteGraphReleaseMaxLat.Value,
                options.RouteGraphReleaseShardSizeDegrees,
                options.RouteGraphReleaseSourceName);
        }

        if (options.RouteGraphWarmupRoutes.Count == 0)
        {
            throw new InvalidOperationException(
                "Set Routing:RouteGraphReleaseMinLon/MinLat/MaxLon/MaxLat or configure Routing:RouteGraphWarmupRoutes before building a graph release.");
        }

        var minLon = options.RouteGraphWarmupRoutes.Min(route => Math.Min(route.StartLng, route.EndLng));
        var minLat = options.RouteGraphWarmupRoutes.Min(route => Math.Min(route.StartLat, route.EndLat));
        var maxLon = options.RouteGraphWarmupRoutes.Max(route => Math.Max(route.StartLng, route.EndLng));
        var maxLat = options.RouteGraphWarmupRoutes.Max(route => Math.Max(route.StartLat, route.EndLat));
        var padding = Math.Clamp(options.RouteGraphReleasePaddingDegrees, 0.001, 1.0);

        return new RouteGraphReleaseBuildRequest(
            minLon - padding,
            minLat - padding,
            maxLon + padding,
            maxLat + padding,
            options.RouteGraphReleaseShardSizeDegrees,
            options.RouteGraphReleaseSourceName);
    }
}
