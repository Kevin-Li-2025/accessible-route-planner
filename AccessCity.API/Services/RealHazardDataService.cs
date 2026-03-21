using System.Globalization;
using AccessCity.API.Models;
using AccessCity.API.Models.External;
using AccessCity.API.Services.External;
using Microsoft.Extensions.Caching.Memory;
using NetTopologySuite.Geometries;
using System.Linq;
using Microsoft.EntityFrameworkCore;

namespace AccessCity.API.Services;

/// <summary>
/// Provides hazard data from real sources (OpenStreetMap via Overpass API).
/// Replaces static/mock data for hazards, dashboard summary, heat-map and infrastructure feed.
/// Results are cached briefly so hazards and dashboard share the same Overpass response.
/// </summary>
public interface IRealHazardDataService
{
    /// <summary>Returns hazards from OSM in the given bbox. Uses default UK (Birmingham) bbox if null.</summary>
    Task<List<HazardReport>> GetActiveHazardsAsync(double? minLat = null, double? minLng = null, double? maxLat = null, double? maxLng = null, HazardStatus? status = null);
}

public class RealHazardDataService : IRealHazardDataService
{
    private readonly IOpenStreetMapClient _openStreetMapClient;
    private readonly IMemoryCache _cache;
    private readonly Data.AppDbContext _dbContext;

    private const string CacheKeyPrefix = "real_hazards:";
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(2);

    // Default bbox: Birmingham area (so we always have a fallback if no bbox provided)
    private const double DefaultMinLat = 52.45;
    private const double DefaultMinLng = -1.95;
    private const double DefaultMaxLat = 52.52;
    private const double DefaultMaxLng = -1.88;

    public RealHazardDataService(IOpenStreetMapClient openStreetMapClient, IMemoryCache cache, Data.AppDbContext dbContext)
    {
        _openStreetMapClient = openStreetMapClient;
        _cache = cache;
        _dbContext = dbContext;
    }

    public async Task<List<HazardReport>> GetActiveHazardsAsync(double? minLat = null, double? minLng = null, double? maxLat = null, double? maxLng = null, HazardStatus? status = null)
    {
        var minLatVal = minLat ?? DefaultMinLat;
        var minLngVal = minLng ?? DefaultMinLng;
        var maxLatVal = maxLat ?? DefaultMaxLat;
        var maxLngVal = maxLng ?? DefaultMaxLng;

        if (minLatVal < -90 || minLatVal > 90 || maxLatVal < -90 || maxLatVal > 90)
            throw new ArgumentException("Latitude values must be between -90 and 90.");
        if (minLngVal < -180 || minLngVal > 180 || maxLngVal < -180 || maxLngVal > 180)
            throw new ArgumentException("Longitude values must be between -180 and 180.");
        if (minLatVal > maxLatVal)
            throw new ArgumentException("minLat must be less than or equal to maxLat.");
        if (minLngVal > maxLngVal)
            throw new ArgumentException("minLng must be less than or equal to maxLng.");

        var cacheKey = $"{CacheKeyPrefix}{minLatVal:F4}_{minLngVal:F4}_{maxLatVal:F4}_{maxLngVal:F4}_{status?.ToString() ?? "all"}";

        if (_cache.TryGetValue(cacheKey, out List<HazardReport>? cached))
            return cached ?? new List<HazardReport>();

        // 1. Fetch from External OSM (only if filtering by Reported or no filter)
        var list = new List<HazardReport>();
        if (status == null || status == HazardStatus.Reported)
        {
            list = await FetchAndMapHazardsAsync(minLatVal, minLngVal, maxLatVal, maxLngVal, DateTime.UtcNow);
        }

        // 2. Fetch from Local Database
        try 
        {
            var dbQuery = _dbContext.Hazards.Where(h => 
                    h.Location.X >= minLngVal && h.Location.X <= maxLngVal && 
                    h.Location.Y >= minLatVal && h.Location.Y <= maxLatVal);

            if (status.HasValue)
            {
                dbQuery = dbQuery.Where(h => h.Status == status.Value);
            }

            var dbHazards = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(dbQuery);
            
            list.AddRange(dbHazards);
        }
        catch (Exception ex)
        {
            // Fail gracefully if DB is down, still return OSM data
            Console.WriteLine($"[DB ERROR] Failed to fetch hazards: {ex.Message}");
        }
        _cache.Set(cacheKey, list, CacheExpiration);

        return list;
    }

    private async Task<List<HazardReport>> FetchAndMapHazardsAsync(
        double minLatVal, double minLngVal, double maxLatVal, double maxLngVal,
        DateTime snapshotUtc)
    {
        var elements = await _openStreetMapClient.GetHazardLikeDataAsync(minLatVal, minLngVal, maxLatVal, maxLngVal);
        if (elements == null || elements.Count == 0)
            return new List<HazardReport>();

        var list = new List<HazardReport>();
        foreach (var el in elements)
        {
            if (!TryGetCoordinate(el, out var lon, out var lat))
                continue;

            var (hazardType, description) = GetTypeAndDescription(el);
            var id = GuidFromOsmId(el.Type, el.Id);

            list.Add(new HazardReport
            {
                Id = id,
                Location = new Point(lon, lat),
                Type = hazardType,
                Description = description,
                PhotoUrl = string.Empty,
                ReportedAt = ResolveOsmReportedAt(el, snapshotUtc),
                Status = HazardStatus.Reported
            });
        }

        return list;
    }

    /// <summary>
    /// Uses Overpass <c>timestamp</c> when present (last OSM DB update for the object).
    /// Otherwise uses <paramref name="snapshotUtc"/> — not a citizen report time, but the time of our Overpass fetch.
    /// </summary>
    private static DateTime ResolveOsmReportedAt(OverpassElement el, DateTime snapshotUtc)
    {
        if (!string.IsNullOrWhiteSpace(el.Timestamp)
            && DateTime.TryParse(el.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
                : parsed.ToUniversalTime();
        }

        return snapshotUtc;
    }

    private static bool TryGetCoordinate(OverpassElement el, out double lon, out double lat)
    {
        if (el.Center != null)
        {
            lon = el.Center.Lon;
            lat = el.Center.Lat;
            return true;
        }
        if (el.Lat != 0 || el.Lon != 0)
        {
            lat = el.Lat;
            lon = el.Lon;
            return true;
        }
        lon = 0;
        lat = 0;
        return false;
    }

    private static (string type, string description) GetTypeAndDescription(OverpassElement el)
    {
        var tags = el.Tags ?? new Dictionary<string, string>();

        if (el.Type == "node" && tags.TryGetValue("barrier", out var barrierVal))
        {
            var desc = $"Barrier: {barrierVal.Replace("_", " ")}";
            return (NormalizeType(barrierVal), desc);
        }
        if (el.Type == "way" && tags.TryGetValue("highway", out var highwayVal) && highwayVal == "steps")
        {
            return ("steps", "Steps (may limit wheelchair access)");
        }
        if (tags.TryGetValue("surface", out var surfaceVal))
        {
            var desc = $"Path surface: {surfaceVal.Replace("_", " ")} (may affect accessibility)";
            return (NormalizeType(surfaceVal), desc);
        }

        var type = el.Type == "way" ? "path" : "barrier";
        return (type, "Accessibility-related feature from OpenStreetMap");
    }

    private static string NormalizeType(string value)
    {
        return value?.Replace("_", "-").ToLowerInvariant() ?? "unknown";
    }

    private static Guid GuidFromOsmId(string osmType, long osmId)
    {
        var bytes = new byte[16];
        var typeBytes = System.Text.Encoding.UTF8.GetBytes(osmType.PadRight(8));
        var idBytes = BitConverter.GetBytes(osmId);
        Array.Copy(typeBytes, 0, bytes, 0, Math.Min(8, typeBytes.Length));
        Array.Copy(idBytes, 0, bytes, 8, 8);
        return new Guid(bytes);
    }
}
