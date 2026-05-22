using AccessCity.API.Messaging;
using AccessCity.API.Data;
using AccessCity.API.Services;
using Microsoft.EntityFrameworkCore;

namespace AccessCity.API.Services.Background;

public class OsmImportBackgroundService : BackgroundService
{
    private readonly IMessageBus _messageBus;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<OsmImportBackgroundService> _logger;
    private readonly AccessCityMetrics _metrics;

    public OsmImportBackgroundService(
        IMessageBus messageBus,
        IServiceProvider serviceProvider,
        ILogger<OsmImportBackgroundService> logger,
        AccessCityMetrics metrics)
    {
        _messageBus = messageBus;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _metrics = metrics;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OSM Import Background Service is starting.");

        await _messageBus.SubscribeAsync<OsmImportStartedEvent>(async @event =>
        {
            _logger.LogInformation(
                "Received OSM import job {JobId} for {CityName} ({FilePath})",
                @event.JobId,
                @event.CityName,
                @event.FilePath);

            using var scope = _serviceProvider.CreateScope();
            var importService = scope.ServiceProvider.GetRequiredService<IOsmImportService>();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var started = DateTime.UtcNow;

            try
            {
                await MarkJobRunningAsync(dbContext, @event.JobId, @event.FilePath, @event.CityName, @event.QueuedAtUtc, started, stoppingToken);
                var result = await importService.ImportAsync(@event.FilePath, stoppingToken);
                await MarkJobCompletedAsync(dbContext, @event.JobId, result.RunId, stoppingToken);
                _metrics.OsmImportCompleted((DateTime.UtcNow - started).TotalMilliseconds, "completed");
                _logger.LogInformation("Successfully completed background OSM import job {JobId}", @event.JobId);
            }
            catch (Exception ex)
            {
                await MarkJobFailedAsync(dbContext, @event.JobId, ex, stoppingToken);
                _metrics.OsmImportCompleted((DateTime.UtcNow - started).TotalMilliseconds, "failed");
                _logger.LogError(ex, "Error during background OSM import job {JobId}", @event.JobId);
                throw;
            }
        }, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
        }
    }

    private static async Task MarkJobRunningAsync(
        AppDbContext dbContext,
        Guid jobId,
        string filePath,
        string cityName,
        DateTime queuedAt,
        DateTime startedAt,
        CancellationToken cancellationToken)
    {
        var job = await dbContext.OsmImportJobs.SingleOrDefaultAsync(j => j.Id == jobId, cancellationToken);
        if (job is null)
        {
            job = new Models.OsmImportJob
            {
                Id = jobId,
                FilePath = filePath,
                CityName = cityName,
                QueuedAtUtc = queuedAt
            };
            dbContext.OsmImportJobs.Add(job);
        }

        job.Status = "running";
        job.StartedAtUtc ??= startedAt;
        job.Attempts++;
        job.ErrorSummary = null;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task MarkJobCompletedAsync(
        AppDbContext dbContext,
        Guid jobId,
        long runId,
        CancellationToken cancellationToken)
    {
        var job = await dbContext.OsmImportJobs.SingleOrDefaultAsync(j => j.Id == jobId, cancellationToken);
        if (job is null)
        {
            return;
        }

        job.Status = "completed";
        job.FinishedAtUtc = DateTime.UtcNow;
        job.FeedIngestionRunId = runId;
        job.ErrorSummary = null;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task MarkJobFailedAsync(
        AppDbContext dbContext,
        Guid jobId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var job = await dbContext.OsmImportJobs.SingleOrDefaultAsync(j => j.Id == jobId, cancellationToken);
        if (job is null)
        {
            return;
        }

        job.Status = "failed";
        job.FinishedAtUtc = DateTime.UtcNow;
        job.ErrorSummary = exception.Message.Length > 2000 ? exception.Message[..2000] : exception.Message;
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
