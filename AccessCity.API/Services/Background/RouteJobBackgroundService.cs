using AccessCity.API.Messaging;

namespace AccessCity.API.Services.Background;

public sealed class RouteJobBackgroundService : BackgroundService
{
    private readonly IMessageBus _messageBus;
    private readonly IRouteJobService _routeJobs;
    private readonly ILogger<RouteJobBackgroundService> _logger;

    public RouteJobBackgroundService(
        IMessageBus messageBus,
        IRouteJobService routeJobs,
        ILogger<RouteJobBackgroundService> logger)
    {
        _messageBus = messageBus;
        _routeJobs = routeJobs;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Route Job Background Service is starting.");

        await _messageBus.SubscribeAsync<RouteJobRequestedEvent>(async @event =>
        {
            _logger.LogInformation("Received route job {JobId}", @event.JobId);
            await _routeJobs.ProcessQueuedJobAsync(@event, stoppingToken);
        }, stoppingToken);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }
}
