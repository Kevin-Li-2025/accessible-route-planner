using System.Text.Json;
using AccessCity.API.Data;
using AccessCity.API.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using NetTopologySuite.Geometries;

namespace AccessCity.API.Services;

public interface ISpatialQueryService
{
    Task<IReadOnlyList<PointOfInterest>> GetPointsOfInterestAsync(
        double lat,
        double lng,
        double radius,
        CancellationToken cancellationToken);

    Task<MapOverlayResponse?> GetMapOverlayAsync(string layerName, CancellationToken cancellationToken);
}

public sealed class SpatialQueryService : ISpatialQueryService
{
    private static readonly HybridCacheEntryOptions PoiCacheOptions = new()
    {
        Expiration = TimeSpan.FromSeconds(30)
    };

    private static readonly GeometryFactory Wgs84 = new(new PrecisionModel(), 4326);

    private readonly AppDbContext _dbContext;
    private readonly HybridCache _cache;

    public SpatialQueryService(AppDbContext dbContext, HybridCache cache)
    {
        _dbContext = dbContext;
        _cache = cache;
    }

    public async Task<IReadOnlyList<PointOfInterest>> GetPointsOfInterestAsync(
        double lat,
        double lng,
        double radius,
        CancellationToken cancellationToken)
    {
        var cacheKey = BuildPoiCacheKey(lat, lng, radius);
#pragma warning disable EXTEXP0018
        var cached = await _cache.GetOrCreateAsync(
            cacheKey,
            async token => await QueryCachedPoiAsync(lat, lng, radius, token),
            PoiCacheOptions,
            cancellationToken: cancellationToken);
#pragma warning restore EXTEXP0018

        return cached.Select(ToPointOfInterest).ToList();
    }

    private async Task<CachedPointOfInterest[]> QueryCachedPoiAsync(
        double lat,
        double lng,
        double radius,
        CancellationToken cancellationToken)
    {
        var assets = await _dbContext.InfrastructureAssets
            .FromSqlInterpolated($"""
                SELECT *
                FROM infrastructure_assets
                WHERE ST_DWithin(
                    "Geometry"::geography,
                    ST_SetSRID(ST_MakePoint({lng}, {lat}), 4326)::geography,
                    {radius})
                ORDER BY ST_Distance(
                    "Geometry"::geography,
                    ST_SetSRID(ST_MakePoint({lng}, {lat}), 4326)::geography)
                LIMIT 100
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return assets.Select(asset =>
        {
            var centroid = asset.Geometry is Point point ? point : asset.Geometry.Centroid;
            return new CachedPointOfInterest(
                GuidFromAssetId(asset.Id),
                asset.Name ?? asset.AssetType,
                asset.AssetType,
                centroid.Y,
                centroid.X,
                ParseTags(asset.AccessibilityInfo));
        }).ToArray();
    }

    public async Task<MapOverlayResponse?> GetMapOverlayAsync(string layerName, CancellationToken cancellationToken)
    {
        if (string.Equals(layerName, "hazards", StringComparison.OrdinalIgnoreCase))
        {
            var hazards = await _dbContext.Hazards
                .AsNoTracking()
                .OrderByDescending(hazard => hazard.ReportedAt)
                .Take(250)
                .ToListAsync(cancellationToken);

            return new MapOverlayResponse
            {
                Layer = "hazards",
                Features = hazards.Select(hazard => new MapOverlayFeature
                {
                    Geometry = hazard.Location,
                    Properties = new
                    {
                        hazard.Id,
                        hazard.Type,
                        Status = hazard.Status.ToString(),
                        hazard.Description,
                        hazard.ReportedAt
                    }
                }).ToList()
            };
        }

        if (string.Equals(layerName, "infrastructure", StringComparison.OrdinalIgnoreCase))
        {
            var assets = await _dbContext.InfrastructureAssets
                .AsNoTracking()
                .OrderByDescending(asset => asset.UpdatedAt)
                .Take(250)
                .ToListAsync(cancellationToken);

            return new MapOverlayResponse
            {
                Layer = "infrastructure",
                Features = assets.Select(asset => new MapOverlayFeature
                {
                    Geometry = asset.Geometry,
                    Properties = new
                    {
                        asset.Id,
                        asset.AssetType,
                        asset.Name,
                        asset.Status,
                        AccessibilityTags = ParseTags(asset.AccessibilityInfo)
                    }
                }).ToList()
            };
        }

        return null;
    }

    private static Dictionary<string, string> ParseTags(JsonDocument json)
    {
        return json.RootElement.ValueKind == JsonValueKind.Object
            ? json.RootElement.EnumerateObject().ToDictionary(prop => prop.Name, prop => prop.Value.ToString())
            : new Dictionary<string, string>();
    }

    private static PointOfInterest ToPointOfInterest(CachedPointOfInterest cached)
    {
        return new PointOfInterest
        {
            Id = cached.Id,
            Name = cached.Name,
            Category = cached.Category,
            Location = Wgs84.CreatePoint(new Coordinate(cached.Longitude, cached.Latitude)),
            AccessibilityTags = cached.AccessibilityTags
        };
    }

    private static string BuildPoiCacheKey(double lat, double lng, double radius)
    {
        var latBucket = Math.Round(lat, 4, MidpointRounding.AwayFromZero);
        var lngBucket = Math.Round(lng, 4, MidpointRounding.AwayFromZero);
        var radiusBucket = (int)Math.Ceiling(Math.Clamp(radius, 1, 5_000) / 50.0) * 50;
        return FormattableString.Invariant($"spatial:poi:v1:{latBucket:F4}:{lngBucket:F4}:{radiusBucket}");
    }

    private static Guid GuidFromAssetId(long id)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes[..8], id);
        return new Guid(bytes);
    }

    private sealed record CachedPointOfInterest(
        Guid Id,
        string Name,
        string Category,
        double Latitude,
        double Longitude,
        Dictionary<string, string> AccessibilityTags);
}
