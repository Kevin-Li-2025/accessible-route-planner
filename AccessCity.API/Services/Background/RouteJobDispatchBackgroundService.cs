using AccessCity.API.Configuration;
using Microsoft.Extensions.Options;

namespace AccessCity.API.Services.Background;

public sealed class RouteJobDispatchBackgroundService : BackgroundService
{
    private readonly IRouteJobDispatchQueue _queue;
    private readonly RoutingOptions _options;
    private readonly ILogger<RouteJobDispatchBackgroundService> _logger;

    public RouteJobDispatchBackgroundService(
        IRouteJobDispatchQueue queue,
        IOptions<RoutingOptions> options,
        ILogger<RouteJobDispatchBackgroundService> logger)
    {
        _queue = queue;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var concurrency = Math.Clamp(_options.RouteJobDispatchConcurrency, 1, 16);
        _logger.LogInformation("Route job dispatch service is starting with concurrency {Concurrency}.", concurrency);

        var workers = Enumerable.Range(0, concurrency)
            .Select(workerId => DispatchLoopAsync(workerId, stoppingToken))
            .ToArray();

        await Task.WhenAll(workers);
    }

    private async Task DispatchLoopAsync(int workerId, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var workItem = await _queue.DequeueDispatchAsync(stoppingToken);
                await _queue.DispatchSubmissionAsync(workItem, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Route job dispatch worker {WorkerId} failed while processing a submission.", workerId);
            }
        }
    }
}
