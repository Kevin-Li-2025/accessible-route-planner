using AccessCity.API.Messaging;
using AccessCity.API.Services;

namespace AccessCity.API.Services.Background;

public class OsmImportBackgroundService : BackgroundService
{
    private readonly IMessageBus _messageBus;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<OsmImportBackgroundService> _logger;

    public OsmImportBackgroundService(
        IMessageBus messageBus,
        IServiceProvider serviceProvider,
        ILogger<OsmImportBackgroundService> logger)
    {
        _messageBus = messageBus;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OSM Import Background Service is starting.");

        await _messageBus.SubscribeAsync<OsmImportStartedEvent>(async @event =>
        {
            _logger.LogInformation("Received OSM Import request for: {CityName} ({FilePath})", @event.CityName, @event.FilePath);

            using var scope = _serviceProvider.CreateScope();
            var importService = scope.ServiceProvider.GetRequiredService<IOsmImportService>();

            try
            {
                await importService.ImportConfiguredAsync(stoppingToken);
                _logger.LogInformation("Successfully completed background OSM import for {CityName}", @event.CityName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during background OSM import for {CityName}", @event.CityName);
            }
        }, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
        }
    }
}
