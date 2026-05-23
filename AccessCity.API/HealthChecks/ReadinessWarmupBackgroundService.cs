namespace AccessCity.API.HealthChecks;

public sealed class ReadinessWarmupBackgroundService : BackgroundService
{
    private readonly CachedReadinessService _readiness;
    private readonly ILogger<ReadinessWarmupBackgroundService> _logger;
    private readonly TimeSpan _refreshInterval;

    public ReadinessWarmupBackgroundService(
        CachedReadinessService readiness,
        IConfiguration configuration,
        ILogger<ReadinessWarmupBackgroundService> logger)
    {
        _readiness = readiness;
        _logger = logger;
        var intervalMs = Math.Max(500, configuration.GetValue("HealthChecks:ReadinessBackgroundRefreshMilliseconds", 2_000));
        _refreshInterval = TimeSpan.FromMilliseconds(intervalMs);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _readiness.RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Readiness snapshot refresh failed.");
            }

            try
            {
                await Task.Delay(_refreshInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
