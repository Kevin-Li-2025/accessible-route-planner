using AccessCity.API.Configuration;
using AccessCity.API.HealthChecks;
using System.Globalization;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;

namespace AccessCity.API.Services.Background;

public sealed class HotPathWarmupBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly HotPathWarmupOptions _options;
    private readonly RoutingOptions _routingOptions;
    private readonly ILogger<HotPathWarmupBackgroundService> _logger;

    public HotPathWarmupBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptions<HotPathWarmupOptions> options,
        IOptions<RoutingOptions> routingOptions,
        ILogger<HotPathWarmupBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _routingOptions = routingOptions.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        var initialDelay = TimeSpan.FromSeconds(Math.Clamp(_options.InitialDelaySeconds, 0, 300));
        if (initialDelay > TimeSpan.Zero)
        {
            await Task.Delay(initialDelay, stoppingToken).ConfigureAwait(false);
        }

        var interval = TimeSpan.FromSeconds(Math.Clamp(_options.IntervalSeconds, 60, 3600));
        using var timer = new PeriodicTimer(interval);

        do
        {
            await WarmHotPathsWithTimeoutAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task WarmHotPathsWithTimeoutAsync(CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 3, 120)));

        try
        {
            await WarmHotPathsAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Hot path warmup timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Hot path warmup failed.");
        }
    }

    private async Task WarmHotPathsAsync(CancellationToken cancellationToken)
    {
        var points = BuildWarmupPoints();
        if (points.Count == 0 && !_options.WarmReadiness)
        {
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var serviceProvider = scope.ServiceProvider;

        if (_options.WarmReadiness)
        {
            var readiness = serviceProvider.GetService<CachedReadinessService>();
            if (readiness is not null)
            {
                await readiness.RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (_options.WarmRisk && points.Count > 0)
        {
            await WarmRiskScoresAsync(serviceProvider, points, cancellationToken).ConfigureAwait(false);
        }

        if (_options.WarmPoi && points.Count > 0)
        {
            await WarmPointsOfInterestAsync(serviceProvider, points, cancellationToken).ConfigureAwait(false);
        }

        if (_options.WarmRouteGraph)
        {
            await WarmRouteGraphsAsync(serviceProvider, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Warmed hot paths for {PointCount} points (readiness={WarmReadiness}, risk={WarmRisk}, poi={WarmPoi}, routeGraph={WarmRouteGraph}).",
            points.Count,
            _options.WarmReadiness,
            _options.WarmRisk,
            _options.WarmPoi,
            _options.WarmRouteGraph);
    }

    private async Task WarmRiskScoresAsync(
        IServiceProvider serviceProvider,
        IReadOnlyList<WarmupPoint> points,
        CancellationToken cancellationToken)
    {
        var hazardQueries = serviceProvider.GetRequiredService<IHazardQueryService>();
        var risk = serviceProvider.GetRequiredService<IRiskScoringService>();
        var riskCache = serviceProvider.GetRequiredService<IRiskScoreCacheService>();
        var radius = Math.Min(
            Math.Max(1, _options.RiskRadiusMetres),
            Math.Max(1, _routingOptions.MaxRiskQueryRadiusMetres));

        foreach (var point in points)
        {
            var cacheKey = riskCache.BuildKey(point.Lat, point.Lng, radius);
            await riskCache.GetOrComputeAsync(
                cacheKey,
                async token =>
                {
                    var hazards = await hazardQueries
                        .LoadHazardsNearPointAsync(point.Lat, point.Lng, radius, token)
                        .ConfigureAwait(false);
                    return await risk
                        .EvaluateRiskAsync(point.Lat, point.Lng, radius, hazards)
                        .ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WarmPointsOfInterestAsync(
        IServiceProvider serviceProvider,
        IReadOnlyList<WarmupPoint> points,
        CancellationToken cancellationToken)
    {
        var spatialQueries = serviceProvider.GetRequiredService<ISpatialQueryService>();
        var radius = Math.Clamp(_options.PoiRadiusMetres, 1, 10_000);

        foreach (var point in points)
        {
            await spatialQueries
                .GetPointsOfInterestAsync(point.Lat, point.Lng, radius, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task WarmRouteGraphsAsync(
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        var routes = _routingOptions.RouteGraphWarmupRoutes
            .Where(IsValidWarmupRoute)
            .Take(Math.Max(1, _options.MaxPoints))
            .ToList();
        if (routes.Count == 0)
        {
            return;
        }

        var repository = serviceProvider.GetRequiredService<IRouteGraphRepository>();
        foreach (var route in routes)
        {
            await repository
                .LoadGraphAsync(
                    new Coordinate(route.StartLng, route.StartLat),
                    new Coordinate(route.EndLng, route.EndLat),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private IReadOnlyList<WarmupPoint> BuildWarmupPoints()
    {
        var maxPoints = Math.Clamp(_options.MaxPoints, 1, 512);
        var seedPoints = new List<WarmupPoint>();

        foreach (var point in _options.Points)
        {
            AddPoint(seedPoints, point.Name, point.Lat, point.Lng);
        }

        foreach (var route in _routingOptions.RouteGraphWarmupRoutes.Where(IsValidWarmupRoute))
        {
            AddPoint(seedPoints, $"{route.Name}:start", route.StartLat, route.StartLng);
            AddPoint(seedPoints, $"{route.Name}:end", route.EndLat, route.EndLng);
            AddPoint(
                seedPoints,
                $"{route.Name}:mid",
                (route.StartLat + route.EndLat) / 2,
                (route.StartLng + route.EndLng) / 2);
        }

        var points = new List<WarmupPoint>(seedPoints);
        var corridorSteps = Math.Clamp(_options.BucketCorridorRadiusSteps, 0, 64);
        var corridorStepDegrees = Math.Clamp(Math.Abs(_options.BucketCorridorStepDegrees), 0.00001, 0.01);
        for (var step = 1; step <= corridorSteps; step++)
        {
            var offset = step * corridorStepDegrees;
            foreach (var seed in seedPoints)
            {
                AddPoint(points, $"{seed.Name}:corridor:{step}:forward", seed.Lat + offset, seed.Lng - offset);
                AddPoint(points, $"{seed.Name}:corridor:{step}:back", seed.Lat - offset, seed.Lng + offset);
            }
        }

        return points
            .GroupBy(
                point => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Math.Round(point.Lat, 4):F4}:{Math.Round(point.Lng, 4):F4}"),
                StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(maxPoints)
            .ToList();
    }

    private static void AddPoint(List<WarmupPoint> points, string name, double lat, double lng)
    {
        if (IsValidLatitude(lat) && IsValidLongitude(lng))
        {
            points.Add(new WarmupPoint(name, lat, lng));
        }
    }

    private static bool IsValidWarmupRoute(RouteGraphWarmupRouteOptions route) =>
        IsValidLatitude(route.StartLat)
        && IsValidLatitude(route.EndLat)
        && IsValidLongitude(route.StartLng)
        && IsValidLongitude(route.EndLng);

    private static bool IsValidLatitude(double value) => value is >= -90 and <= 90;

    private static bool IsValidLongitude(double value) => value is >= -180 and <= 180;

    private sealed record WarmupPoint(string Name, double Lat, double Lng);
}
