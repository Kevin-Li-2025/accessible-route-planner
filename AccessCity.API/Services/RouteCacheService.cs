using Microsoft.Extensions.Caching.Hybrid;

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
