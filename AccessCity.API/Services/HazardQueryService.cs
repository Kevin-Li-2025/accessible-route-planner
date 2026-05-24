using AccessCity.API.Configuration;
using AccessCity.API.Data;
using AccessCity.API.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AccessCity.API.Services;

public interface IHazardQueryService
{
    Task<List<HazardReport>> LoadHazardsForRouteAsync(RouteRequest request, CancellationToken cancellationToken);

    Task<List<HazardReport>> LoadHazardsNearPointAsync(
        double latitude,
        double longitude,
        double radiusMetres,
        CancellationToken cancellationToken);

    Task<List<HazardReport>> LoadActiveHazardsAsync(CancellationToken cancellationToken);
}

public sealed class HazardQueryService : IHazardQueryService
{
    private readonly AppDbContext _dbContext;
    private readonly IHazardSpatialIndex _spatialIndex;
    private readonly RoutingOptions _routingOptions;

    public HazardQueryService(
        AppDbContext dbContext,
        IHazardSpatialIndex spatialIndex,
        IOptions<RoutingOptions> routingOptions)
    {
        _dbContext = dbContext;
        _spatialIndex = spatialIndex;
        _routingOptions = routingOptions.Value;
    }

    public async Task<List<HazardReport>> LoadHazardsForRouteAsync(
        RouteRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Start is null || request.End is null)
        {
            return await LoadActiveHazardsAsync(cancellationToken);
        }

        var paddingMetres = Math.Max(0, _routingOptions.HazardQueryPaddingMetres);
        var latitudePadding = MetresToLatitudeDegrees(paddingMetres);
        var centerLatitude = (request.Start.Y + request.End.Y) / 2.0;
        var longitudePadding = MetresToLongitudeDegrees(paddingMetres, centerLatitude);

        var minLon = Math.Min(request.Start.X, request.End.X) - longitudePadding;
        var maxLon = Math.Max(request.Start.X, request.End.X) + longitudePadding;
        var minLat = Math.Min(request.Start.Y, request.End.Y) - latitudePadding;
        var maxLat = Math.Max(request.Start.Y, request.End.Y) + latitudePadding;
        var limit = Math.Max(1, _routingOptions.MaxHazardsPerRequest);

        // Fast-path: use the in-memory R-Tree spatial index when warmed up.
        // This avoids hitting Postgres entirely on the routing hot path.
        if (_spatialIndex.IsWarmedUp)
        {
            return _spatialIndex.QueryBoundingBox(minLon, minLat, maxLon, maxLat)
                .Take(limit)
                .ToList();
        }

        if (_dbContext.Database.IsRelational())
        {
            return await _dbContext.Hazards
                .FromSqlInterpolated($"""
                    SELECT *
                    FROM public.hazard_report
                    WHERE status IN ('reported'::hazard_status, 'under_review'::hazard_status)
                      AND geom && ST_MakeEnvelope({minLon}, {minLat}, {maxLon}, {maxLat}, 4326)
                    ORDER BY reported_at DESC
                    LIMIT {limit}
                    """)
                .AsNoTracking()
                .ToListAsync(cancellationToken);
        }

        return (await LoadActiveHazardsAsync(cancellationToken))
            .Where(h => h.Location is not null
                        && h.Location.X >= minLon
                        && h.Location.X <= maxLon
                        && h.Location.Y >= minLat
                        && h.Location.Y <= maxLat)
            .Take(limit)
            .ToList();
    }

    public async Task<List<HazardReport>> LoadHazardsNearPointAsync(
        double latitude,
        double longitude,
        double radiusMetres,
        CancellationToken cancellationToken)
    {
        var cappedRadius = Math.Min(
            Math.Max(0, radiusMetres),
            Math.Max(1, _routingOptions.MaxRiskQueryRadiusMetres));
        var queryRadius = cappedRadius + Math.Max(0, _routingOptions.HazardQueryPaddingMetres);
        var limit = Math.Max(1, _routingOptions.MaxHazardsPerRequest);

        // Fast-path: use the in-memory R-Tree spatial index when warmed up.
        if (_spatialIndex.IsWarmedUp)
        {
            return _spatialIndex.QueryNearby(latitude, longitude, queryRadius)
                .Take(limit)
                .ToList();
        }

        if (_dbContext.Database.IsRelational())
        {
            return await _dbContext.Hazards
                .FromSqlInterpolated($"""
                    SELECT *
                    FROM public.hazard_report
                    WHERE status IN ('reported'::hazard_status, 'under_review'::hazard_status)
                      AND ST_DWithin(
                          geom::geography,
                          ST_SetSRID(ST_MakePoint({longitude}, {latitude}), 4326)::geography,
                          {queryRadius})
                    ORDER BY reported_at DESC
                    LIMIT {limit}
                    """)
                .AsNoTracking()
                .ToListAsync(cancellationToken);
        }

        return (await LoadActiveHazardsAsync(cancellationToken))
            .Where(h => h.Location is not null
                        && HaversineMetres(latitude, longitude, h.Location.Y, h.Location.X) <= queryRadius)
            .Take(limit)
            .ToList();
    }

    public async Task<List<HazardReport>> LoadActiveHazardsAsync(CancellationToken cancellationToken)
    {
        // Prefer the in-memory snapshot when available to avoid hitting Postgres.
        if (_spatialIndex.IsWarmedUp)
        {
            return _spatialIndex.GetAllActive().ToList();
        }

        return await _dbContext.Hazards
            .Where(h => h.Status == HazardStatus.Reported || h.Status == HazardStatus.UnderReview)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    private static double MetresToLatitudeDegrees(double metres) => metres / 111_320.0;

    private static double MetresToLongitudeDegrees(double metres, double latitude)
    {
        var radians = latitude * Math.PI / 180.0;
        var metresPerDegree = 111_320.0 * Math.Max(0.1, Math.Cos(radians));
        return metres / metresPerDegree;
    }

    private static double HaversineMetres(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadius = 6_371_000.0;
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2))
                * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return earthRadius * c;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
