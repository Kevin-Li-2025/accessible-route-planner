using System.Collections.Concurrent;
using System.Globalization;
using AccessCity.API.Models;
using AccessCity.API.Models.External;
using AccessCity.API.Services.External;
using Microsoft.Extensions.Caching.Memory;
using NetTopologySuite.Geometries;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using System.Threading;

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
    private readonly ILogger<RealHazardDataService> _logger;
    private readonly bool _realtimeOverpassEnabled;
    private readonly TimeSpan _osmFetchBudget;
    private static readonly ConcurrentDictionary<string, Lazy<Task<List<HazardReport>>>> InFlightLoads = new();

    private const string CacheKeyPrefix = "real_hazards:";
    /// <summary>Overpass is slow; longer cache reduces cold-cache storms on /hazards.</summary>
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(15);

    /// <summary>Hard cap on OSM-derived rows merged into one response (serialization + mobile memory).</summary>
    private const int MaxOsmHazardsPerResponse = 2500;

    /// <summary>Safety valve for DB rows in bbox (misconfigured client or huge imports).</summary>
    private const int MaxDbHazardsPerResponse = 5000;

    // Default bbox: Birmingham area (so we always have a fallback if no bbox provided)
    private const double DefaultMinLat = 52.45;
    private const double DefaultMinLng = -1.95;
    private const double DefaultMaxLat = 52.52;
    private const double DefaultMaxLng = -1.88;
    private static readonly GeometryFactory Wgs84 = new(new PrecisionModel(), 4326);

    public RealHazardDataService(
        IOpenStreetMapClient openStreetMapClient,
        IMemoryCache cache,
        Data.AppDbContext dbContext,
        ILogger<RealHazardDataService> logger,
        IConfiguration configuration)
    {
        _openStreetMapClient = openStreetMapClient;
        _cache = cache;
        _dbContext = dbContext;
        _logger = logger;
        _realtimeOverpassEnabled = configuration.GetValue("ExternalApis:Overpass:RealtimeHazardsEnabled", true);
        _osmFetchBudget = TimeSpan.FromSeconds(
            Math.Max(1, configuration.GetValue("ExternalApis:Overpass:HazardFetchBudgetSeconds", 12)));
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

        var cacheKey = $"{CacheKeyPrefix}{minLatVal:F4}_{minLngVal:F4}_{maxLatVal:F4}_{maxLngVal:F4}_{status?.ToString() ?? "all"}_osm:{_realtimeOverpassEnabled}";

        if (_cache.TryGetValue(cacheKey, out List<HazardReport>? cached))
            return cached ?? new List<HazardReport>();

        var lazyLoad = InFlightLoads.GetOrAdd(
            cacheKey,
            _ => new Lazy<Task<List<HazardReport>>>(
                () => LoadAndCacheHazardsAsync(
                    cacheKey,
                    minLatVal,
                    minLngVal,
                    maxLatVal,
                    maxLngVal,
                    status),
                LazyThreadSafetyMode.ExecutionAndPublication));

        var loadTask = lazyLoad.Value;
        try
        {
            return await loadTask.ConfigureAwait(false);
        }
        finally
        {
            if (loadTask.IsCompleted)
            {
                InFlightLoads.TryRemove(cacheKey, out _);
            }
        }
    }

    private async Task<List<HazardReport>> LoadAndCacheHazardsAsync(
        string cacheKey,
        double minLatVal,
        double minLngVal,
        double maxLatVal,
        double maxLngVal,
        HazardStatus? status)
    {
        if (_cache.TryGetValue(cacheKey, out List<HazardReport>? cached))
            return cached ?? new List<HazardReport>();

        var includeOsm = _realtimeOverpassEnabled && (status == null || status == HazardStatus.Reported);
        var osmTask = includeOsm
            ? FetchAndMapHazardsAsync(minLatVal, minLngVal, maxLatVal, maxLngVal, DateTime.UtcNow)
            : Task.FromResult(new List<HazardReport>());

        var dbTask = FetchDbHazardsInBBoxAsync(minLatVal, minLngVal, maxLatVal, maxLngVal, status);

        await Task.WhenAll(osmTask, dbTask).ConfigureAwait(false);

        var list = new List<HazardReport>();
        list.AddRange(await osmTask.ConfigureAwait(false));
        list.AddRange(await dbTask.ConfigureAwait(false));

        _cache.Set(cacheKey, list, CacheExpiration);

        return list;
    }

    private async Task<List<HazardReport>> FetchDbHazardsInBBoxAsync(
        double minLatVal,
        double minLngVal,
        double maxLatVal,
        double maxLngVal,
        HazardStatus? status)
    {
        try
        {
            var envelope = Wgs84.CreatePolygon(new[]
            {
                new Coordinate(minLngVal, minLatVal),
                new Coordinate(maxLngVal, minLatVal),
                new Coordinate(maxLngVal, maxLatVal),
                new Coordinate(minLngVal, maxLatVal),
                new Coordinate(minLngVal, minLatVal)
            });

            var dbQuery = _dbContext.Hazards
                .AsNoTracking()
                .Where(h => h.Location.Intersects(envelope));

            if (status.HasValue)
                dbQuery = dbQuery.Where(h => h.Status == status.Value);

            return await dbQuery
                .OrderByDescending(h => h.ReportedAt)
                .Take(MaxDbHazardsPerResponse)
                .ToListAsync()
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch DB hazards for bbox.");
            return new List<HazardReport>();
        }
    }

    private async Task<List<HazardReport>> FetchAndMapHazardsAsync(
        double minLatVal, double minLngVal, double maxLatVal, double maxLngVal,
        DateTime snapshotUtc)
    {
        using var budget = new CancellationTokenSource(_osmFetchBudget);
        List<OverpassElement>? elements;
        try
        {
            elements = await _openStreetMapClient
                .GetHazardLikeDataAsync(minLatVal, minLngVal, maxLatVal, maxLngVal, budget.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex,
                "Overpass hazard fetch timed out after {Budget}s; returning DB hazards only for bbox.",
                _osmFetchBudget.TotalSeconds);
            return new List<HazardReport>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Overpass hazard fetch failed; returning DB hazards only for bbox.");
            return new List<HazardReport>();
        }

        if (elements == null || elements.Count == 0)
            return new List<HazardReport>();

        var list = new List<HazardReport>(Math.Min(elements.Count, MaxOsmHazardsPerResponse));
        foreach (var el in elements)
        {
            if (list.Count >= MaxOsmHazardsPerResponse)
            {
                _logger.LogWarning(
                    "OSM hazard merge truncated at {Cap} elements for bbox ({MinLat},{MinLng})-({MaxLat},{MaxLng}).",
                    MaxOsmHazardsPerResponse, minLatVal, minLngVal, maxLatVal, maxLngVal);
                break;
            }

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
