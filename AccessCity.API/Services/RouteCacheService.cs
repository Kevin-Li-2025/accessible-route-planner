using System.Text.Json;
using System.Text.Json.Serialization;
using AccessCity.API.Serialization;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using NetTopologySuite.IO.Converters;

namespace AccessCity.API.Services;

/// <summary>
/// Content-addressable route response cache.
/// Hashes (start, end, profile, safetyWeight) → cached RouteResponse.
/// Prevents redundant OSRM calls and risk scoring for identical requests.
/// </summary>
public interface IRouteCacheService
{
    Task<Models.RouteResponse?> TryGetAsync(string cacheKey);
    Task SetAsync(string cacheKey, Models.RouteResponse response);
    string BuildKey(double startLat, double startLng, double endLat, double endLng,
                        string profile, double safetyWeight);
}

public interface IRouteOptionsCacheService
{
    Task<Models.SafePathOptionsResponse?> TryGetAsync(string cacheKey);
    Task SetAsync(string cacheKey, Models.SafePathOptionsResponse response);
    string BuildKey(double startLat, double startLng, double endLat, double endLng,
                    string profile, double safetyWeight);
}

public class RouteCacheService : IRouteCacheService
{
    private readonly HybridCache _cache;
    /// <summary>Repeat identical safe-path requests avoid OSRM within this window (see also RoutingService).</summary>
    private static readonly TimeSpan RouteTtl = TimeSpan.FromMinutes(15);

    public RouteCacheService(HybridCache cache)
    {
        _cache = cache;
    }

    public async Task<Models.RouteResponse?> TryGetAsync(string cacheKey)
    {
#pragma warning disable EXTEXP0018
        // HybridCache.GetOrCreateAsync always creates; we use a wrapper pattern instead.
        return await _cache.GetOrCreateAsync<Models.RouteResponse?>(
            cacheKey,
            _ => ValueTask.FromResult<Models.RouteResponse?>(null),
            new HybridCacheEntryOptions { Expiration = RouteTtl });
#pragma warning restore EXTEXP0018
    }

    public async Task SetAsync(string cacheKey, Models.RouteResponse response)
    {
#pragma warning disable EXTEXP0018
        await _cache.SetAsync(cacheKey, response,
            new HybridCacheEntryOptions { Expiration = RouteTtl });
#pragma warning restore EXTEXP0018
    }

    public string BuildKey(double startLat, double startLng, double endLat, double endLng,
                           string profile, double safetyWeight)
    {
        return $"route:{startLat:F5}:{startLng:F5}:{endLat:F5}:{endLng:F5}:{profile}:{safetyWeight:F2}";
    }
}

public class RouteOptionsCacheService : IRouteOptionsCacheService
{
    private readonly IMemoryCache _memoryCache;
    private readonly IDistributedCache _distributedCache;
    private readonly ILogger<RouteOptionsCacheService> _logger;
    private static readonly TimeSpan OptionsTtl = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions CacheJsonOptions = CreateCacheJsonOptions();

    public RouteOptionsCacheService(
        IMemoryCache memoryCache,
        IDistributedCache distributedCache,
        ILogger<RouteOptionsCacheService> logger)
    {
        _memoryCache = memoryCache;
        _distributedCache = distributedCache;
        _logger = logger;
    }

    public async Task<Models.SafePathOptionsResponse?> TryGetAsync(string cacheKey)
    {
        if (_memoryCache.TryGetValue(cacheKey, out Models.SafePathOptionsResponse? cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            var json = await _distributedCache.GetStringAsync(cacheKey);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var response = JsonSerializer.Deserialize<Models.SafePathOptionsResponse>(json, CacheJsonOptions);
            if (response is null)
            {
                return null;
            }

            _memoryCache.Set(cacheKey, response, OptionsTtl);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Route options cache key {CacheKey} could not be read.", cacheKey);
            return null;
        }
    }

    public async Task SetAsync(string cacheKey, Models.SafePathOptionsResponse response)
    {
        _memoryCache.Set(cacheKey, response, OptionsTtl);

        try
        {
            var json = JsonSerializer.Serialize(response, CacheJsonOptions);
            await _distributedCache.SetStringAsync(
                cacheKey,
                json,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = OptionsTtl });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Route options cache key {CacheKey} could not be written.", cacheKey);
        }
    }

    public string BuildKey(
        double startLat,
        double startLng,
        double endLat,
        double endLng,
        string profile,
        double safetyWeight)
    {
        return $"route_options:v1:{startLat:F5}:{startLng:F5}:{endLat:F5}:{endLng:F5}:{profile}:{safetyWeight:F2}";
    }

    private static JsonSerializerOptions CreateCacheJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
        options.Converters.Add(new CoordinateJsonConverter());
        options.Converters.Add(new GeoJsonConverterFactory());
        return options;
    }
}
