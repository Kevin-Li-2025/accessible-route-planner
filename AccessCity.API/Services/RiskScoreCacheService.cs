using System.Globalization;
using AccessCity.API.Models;
using Microsoft.Extensions.Caching.Hybrid;

namespace AccessCity.API.Services;

public interface IRiskScoreCacheService
{
    Task<RiskScoreResponse> GetOrComputeAsync(
        string cacheKey,
        Func<CancellationToken, Task<RiskScoreResponse>> factory,
        CancellationToken cancellationToken = default);

    string BuildKey(double latitude, double longitude, double radiusMetres);
}

public sealed class RiskScoreCacheService : IRiskScoreCacheService
{
    private readonly HybridCache _cache;
    private readonly TimeSpan _ttl;
    private readonly int _coordinatePrecision;
    private readonly int _radiusBucketMetres;

    public RiskScoreCacheService(HybridCache cache, IConfiguration configuration)
    {
        _cache = cache;
        _ttl = TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue("RiskScoring:CacheTtlSeconds", 30)));
        _coordinatePrecision = Math.Clamp(configuration.GetValue("RiskScoring:CacheCoordinatePrecision", 4), 3, 6);
        _radiusBucketMetres = Math.Max(1, configuration.GetValue("RiskScoring:CacheRadiusBucketMetres", 50));
    }

    public async Task<RiskScoreResponse> GetOrComputeAsync(
        string cacheKey,
        Func<CancellationToken, Task<RiskScoreResponse>> factory,
        CancellationToken cancellationToken = default)
    {
#pragma warning disable EXTEXP0018
        return await _cache.GetOrCreateAsync(
            cacheKey,
            async token => await factory(token),
            new HybridCacheEntryOptions { Expiration = _ttl },
            cancellationToken: cancellationToken);
#pragma warning restore EXTEXP0018
    }

    public string BuildKey(double latitude, double longitude, double radiusMetres)
    {
        var roundedLatitude = Math.Round(latitude, _coordinatePrecision, MidpointRounding.AwayFromZero);
        var roundedLongitude = Math.Round(longitude, _coordinatePrecision, MidpointRounding.AwayFromZero);
        var radiusBucket = (int)Math.Ceiling(Math.Max(0, radiusMetres) / _radiusBucketMetres) * _radiusBucketMetres;
        var coordinateFormat = "F" + _coordinatePrecision.ToString(CultureInfo.InvariantCulture);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"risk-score:{roundedLatitude.ToString(coordinateFormat, CultureInfo.InvariantCulture)}:{roundedLongitude.ToString(coordinateFormat, CultureInfo.InvariantCulture)}:{radiusBucket}");
    }
}
