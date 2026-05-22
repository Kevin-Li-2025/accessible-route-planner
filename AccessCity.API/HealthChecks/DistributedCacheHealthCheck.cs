using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AccessCity.API.HealthChecks;

public sealed class DistributedCacheHealthCheck : IHealthCheck
{
    private static readonly byte[] Payload = "ok"u8.ToArray();
    private readonly IDistributedCache _cache;

    public DistributedCacheHealthCheck(IDistributedCache cache)
    {
        _cache = cache;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var key = $"health:cache:{Guid.NewGuid():N}";
        try
        {
            await _cache.SetAsync(
                key,
                Payload,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(15) },
                cancellationToken);
            var value = await _cache.GetAsync(key, cancellationToken);
            return value is { Length: > 0 }
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Degraded("Cache write succeeded but read returned no value.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Distributed cache is not reachable.", ex);
        }
    }
}
