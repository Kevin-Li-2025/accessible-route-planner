using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AccessCity.API.HealthChecks;

public sealed class CachedReadinessService
{
    private readonly HealthCheckService _healthChecks;
    private readonly ILogger<CachedReadinessService> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly TimeSpan _ttl;
    private CachedReadinessResult? _cached;
    private int _backgroundRefreshQueued;

    public CachedReadinessService(
        HealthCheckService healthChecks,
        IConfiguration configuration,
        ILogger<CachedReadinessService> logger)
    {
        _healthChecks = healthChecks;
        _logger = logger;
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

        if (cached is not null)
        {
            QueueBackgroundRefresh();
            return cached.Report;
        }

        return await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<HealthReport> RefreshAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var cached = _cached;
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

    private void QueueBackgroundRefresh()
    {
        if (Interlocked.Exchange(ref _backgroundRefreshQueued, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await RefreshAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Readiness background refresh timed out.");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Readiness background refresh failed.");
            }
            finally
            {
                Interlocked.Exchange(ref _backgroundRefreshQueued, 0);
            }
        });
    }

    private sealed record CachedReadinessResult(HealthReport Report, DateTimeOffset ExpiresAt);
}
