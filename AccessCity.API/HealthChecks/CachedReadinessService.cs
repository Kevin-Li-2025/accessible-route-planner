using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AccessCity.API.HealthChecks;

public sealed class CachedReadinessService
{
    private readonly HealthCheckService _healthChecks;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly TimeSpan _ttl;
    private CachedReadinessResult? _cached;

    public CachedReadinessService(HealthCheckService healthChecks, IConfiguration configuration)
    {
        _healthChecks = healthChecks;
        var ttlMs = Math.Max(250, configuration.GetValue("HealthChecks:ReadinessCacheMilliseconds", 2_000));
        _ttl = TimeSpan.FromMilliseconds(ttlMs);
    }

    public async Task<HealthReport> CheckAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var cached = _cached;
        if (cached is not null && cached.ExpiresAt > now)
        {
            return cached.Report;
        }

        if (!await _refreshLock.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            cached = _cached;
            if (cached is not null)
            {
                return cached.Report;
            }

            await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            now = DateTimeOffset.UtcNow;
            cached = _cached;
            if (cached is not null && cached.ExpiresAt > now)
            {
                return cached.Report;
            }

            var report = await _healthChecks
                .CheckHealthAsync(check => check.Tags.Contains("ready"), cancellationToken)
                .ConfigureAwait(false);
            _cached = new CachedReadinessResult(report, now.Add(_ttl));
            return report;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private sealed record CachedReadinessResult(HealthReport Report, DateTimeOffset ExpiresAt);
}
