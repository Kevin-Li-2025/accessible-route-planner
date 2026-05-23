using Microsoft.Extensions.Options;

namespace AccessCity.API.Messaging.Kafka;

public sealed class KafkaTopicWarmupBackgroundService : BackgroundService
{
    private readonly IKafkaTopicInitializer _topicInitializer;
    private readonly KafkaOptions _options;
    private readonly ILogger<KafkaTopicWarmupBackgroundService> _logger;

    public KafkaTopicWarmupBackgroundService(
        IKafkaTopicInitializer topicInitializer,
        IOptions<KafkaOptions> options,
        ILogger<KafkaTopicWarmupBackgroundService> logger)
    {
        _topicInitializer = topicInitializer;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(_options.TopicWarmupTimeoutSeconds, 1, 60));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            _logger.LogInformation("Kafka topic warmup is starting with a {TimeoutSeconds}s timeout.", timeout.TotalSeconds);
            await _topicInitializer.EnsureInfrastructureAsync(CancellationToken.None)
                .WaitAsync(timeoutCts.Token)
                .ConfigureAwait(false);
            _logger.LogInformation("Kafka route and import topics warmed before first request.");
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning("Kafka topic warmup exceeded {TimeoutSeconds}s; first publish may finish topic setup.", timeout.TotalSeconds);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Kafka topic warmup failed; first publish will retry topic setup.");
        }
    }
}
