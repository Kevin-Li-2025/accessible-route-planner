using AccessCity.API.Serialization;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Options;
using NetTopologySuite.IO.Converters;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace AccessCity.API.Messaging.Kafka;

public class KafkaMessageBus : IMessageBus, IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly IIntegrationMessageStore _messageStore;
    private readonly Services.AccessCityMetrics _metrics;
    private readonly ILogger<KafkaMessageBus> _logger;
    private readonly KafkaOptions _options;
    private readonly ConcurrentDictionary<string, Task> _topicCreationTasks = new();
    private static readonly JsonSerializerOptions EventJsonOptions = CreateEventJsonOptions();

    public KafkaMessageBus(
        IOptions<KafkaOptions> options,
        IIntegrationMessageStore messageStore,
        Services.AccessCityMetrics metrics,
        ILogger<KafkaMessageBus> logger)
    {
        _logger = logger;
        _options = options.Value;
        _messageStore = messageStore;
        _metrics = metrics;

        var config = new ProducerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            ClientId = "AccessCity.API",
            Acks = Acks.All,
            EnableIdempotence = true
        };
        _producer = new ProducerBuilder<string, string>(config).Build();
    }

    public async Task PublishAsync<T>(T @event, CancellationToken cancellationToken = default) where T : IntegrationEvent
    {
        var topic = TopicFor<T>();
        await EnsureTopicsAsync([topic], cancellationToken).ConfigureAwait(false);
        var key = @event is IKeyedIntegrationEvent keyedEvent && !string.IsNullOrWhiteSpace(keyedEvent.PartitionKey)
            ? keyedEvent.PartitionKey
            : @event.Id.ToString();
        var message = new Message<string, string>
        {
            Key = key,
            Value = JsonSerializer.Serialize(@event, EventJsonOptions),
            Headers = new Headers
            {
                { "accesscity-event-type", System.Text.Encoding.UTF8.GetBytes(typeof(T).FullName ?? typeof(T).Name) },
                { "accesscity-attempt", System.Text.Encoding.UTF8.GetBytes("1") }
            }
        };

        await _producer.ProduceAsync(topic, message, cancellationToken);
    }

    public Task SubscribeAsync<T>(Func<T, Task> handler, CancellationToken cancellationToken = default) where T : IntegrationEvent
    {
        var topic = TopicFor<T>();
        _ = Task.Run(() => ConsumeLoopAsync(topic, handler, cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(2));
        _producer.Dispose();
    }

    private async Task ConsumeLoopAsync<T>(string topic, Func<T, Task> handler, CancellationToken cancellationToken)
        where T : IntegrationEvent
    {
        var retryTopic = RetryTopic(topic);
        var deadLetterTopic = DeadLetterTopic(topic);
        await EnsureTopicsAsync([topic, retryTopic, deadLetterTopic], cancellationToken).ConfigureAwait(false);
        var config = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = _options.ConsumerGroupId,
            ClientId = $"AccessCity.API.{typeof(T).Name}.consumer",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(new[] { topic, retryTopic });
        _logger.LogInformation(
            "Kafka consumer subscribed to {Topic} and {RetryTopic} in group {GroupId}",
            topic,
            retryTopic,
            _options.ConsumerGroupId);

        while (!cancellationToken.IsCancellationRequested)
        {
            ConsumeResult<string, string>? result = null;
            try
            {
                result = consumer.Consume(cancellationToken);
                if (result is null)
                {
                    continue;
                }

                var messageId = BuildMessageIdentity(topic, result);

                if (await _messageStore.HasProcessedAsync(messageId, topic, _options.ConsumerGroupId, cancellationToken)
                        .ConfigureAwait(false))
                {
                    _logger.LogInformation(
                        "Skipping duplicate Kafka message {MessageId} from {Topic} at {Offset}",
                        messageId,
                        result.Topic,
                        result.Offset);
                    consumer.Commit(result);
                    continue;
                }

                var @event = JsonSerializer.Deserialize<T>(result.Message.Value, EventJsonOptions);
                if (@event is null)
                {
                    _logger.LogWarning("Skipping null Kafka event on {Topic} at {Offset}", topic, result.Offset);
                    await SendToDeadLetterAsync(
                        deadLetterTopic,
                        result,
                        typeof(T).Name,
                        "Deserialized event was null.",
                        cancellationToken).ConfigureAwait(false);
                    consumer.Commit(result);
                    continue;
                }

                await handler(@event).ConfigureAwait(false);
                await _messageStore.MarkProcessedAsync(
                    messageId,
                    topic,
                    _options.ConsumerGroupId,
                    typeof(T).FullName ?? typeof(T).Name,
                    cancellationToken).ConfigureAwait(false);
                consumer.Commit(result);
                _metrics.KafkaProcessed(result.Topic);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (JsonException ex) when (result is not null)
            {
                _logger.LogError(
                    ex,
                    "Kafka message on {Topic} at {Offset} could not be deserialized; moving to DLQ.",
                    result.Topic,
                    result.Offset);
                try
                {
                    await SendToDeadLetterAsync(
                        deadLetterTopic,
                        result,
                        typeof(T).Name,
                        ex.Message,
                        cancellationToken).ConfigureAwait(false);
                    consumer.Commit(result);
                    _metrics.KafkaDeadLettered(deadLetterTopic);
                }
                catch (Exception routeEx) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogError(
                        routeEx,
                        "Failed to route malformed Kafka message from {Topic} at {Offset} to DLQ. Offset was not committed.",
                        result.Topic,
                        result.Offset);
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                if (result is not null)
                {
                    _metrics.KafkaFailed(result.Topic);
                    try
                    {
                        await RouteFailedMessageAsync(
                            result,
                            retryTopic,
                            deadLetterTopic,
                            typeof(T).Name,
                            ex,
                            cancellationToken).ConfigureAwait(false);
                        consumer.Commit(result);
                    }
                    catch (Exception routeEx) when (!cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogError(
                            routeEx,
                            "Failed to route Kafka message from {Topic} at {Offset} after handler failure. Offset was not committed.",
                            result.Topic,
                            result.Offset);
                        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                _logger.LogError(
                    ex,
                    "Kafka consumer failed before receiving a message from {Topic}.",
                    topic);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
        }

        consumer.Close();
    }

    private Task EnsureTopicsAsync(IEnumerable<string> topics, CancellationToken cancellationToken)
    {
        var tasks = topics
            .Where(topic => !string.IsNullOrWhiteSpace(topic))
            .Distinct(StringComparer.Ordinal)
            .Select(topic => _topicCreationTasks.GetOrAdd(topic, _ => CreateTopicAsync(topic, cancellationToken)));

        return Task.WhenAll(tasks);
    }

    private async Task CreateTopicAsync(string topic, CancellationToken cancellationToken)
    {
        var adminConfig = new AdminClientConfig
        {
            BootstrapServers = _options.BootstrapServers
        };

        using var adminClient = new AdminClientBuilder(adminConfig).Build();
        try
        {
            await adminClient.CreateTopicsAsync(
                [
                    new TopicSpecification
                    {
                        Name = topic,
                        NumPartitions = Math.Max(1, _options.TopicPartitions),
                        ReplicationFactor = Math.Max((short)1, _options.TopicReplicationFactor)
                    }
                ],
                new CreateTopicsOptions
                {
                    RequestTimeout = TimeSpan.FromSeconds(10),
                    OperationTimeout = TimeSpan.FromSeconds(10)
                }).WaitAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Ensured Kafka topic {Topic} with {Partitions} partitions and replication factor {ReplicationFactor}.",
                topic,
                Math.Max(1, _options.TopicPartitions),
                Math.Max((short)1, _options.TopicReplicationFactor));
        }
        catch (CreateTopicsException ex) when (ex.Results.All(result => result.Error.Code == ErrorCode.TopicAlreadyExists))
        {
            _logger.LogDebug("Kafka topic {Topic} already exists.", topic);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _topicCreationTasks.TryRemove(topic, out _);
            _logger.LogWarning(ex, "Could not ensure Kafka topic {Topic}; continuing and relying on broker-side topic policy.", topic);
        }
    }

    private async Task RouteFailedMessageAsync<T>(
        ConsumeResult<string, string> result,
        string retryTopic,
        string deadLetterTopic,
        string eventType,
        T exception,
        CancellationToken cancellationToken)
        where T : Exception
    {
        var attempt = ReadAttempt(result.Message.Headers);
        if (attempt < Math.Max(1, _options.MaxProcessingAttempts))
        {
            var nextAttempt = attempt + 1;
            _logger.LogWarning(
                exception,
                "Kafka handler failed for {Topic} at {Offset}; routing to {RetryTopic} for attempt {Attempt}/{MaxAttempts}.",
                result.Topic,
                result.Offset,
                retryTopic,
                nextAttempt,
                _options.MaxProcessingAttempts);

            var delay = TimeSpan.FromSeconds(Math.Max(0, _options.RetryDelaySeconds));
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            await _producer.ProduceAsync(
                retryTopic,
                CreateForwardedMessage(result, nextAttempt, exception.Message, eventType),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        _logger.LogError(
            exception,
            "Kafka handler exhausted {MaxAttempts} attempts for {Topic} at {Offset}; moving to {DeadLetterTopic}.",
            _options.MaxProcessingAttempts,
            result.Topic,
            result.Offset,
            deadLetterTopic);

        await SendToDeadLetterAsync(
            deadLetterTopic,
            result,
            eventType,
            exception.Message,
            cancellationToken).ConfigureAwait(false);
        _metrics.KafkaDeadLettered(deadLetterTopic);
    }

    private Task SendToDeadLetterAsync(
        string deadLetterTopic,
        ConsumeResult<string, string> result,
        string eventType,
        string error,
        CancellationToken cancellationToken)
    {
        return _producer.ProduceAsync(
            deadLetterTopic,
            CreateForwardedMessage(result, ReadAttempt(result.Message.Headers), error, eventType, deadLetter: true),
            cancellationToken);
    }

    private static Message<string, string> CreateForwardedMessage(
        ConsumeResult<string, string> result,
        int attempt,
        string error,
        string eventType,
        bool deadLetter = false)
    {
        var headers = new Headers
        {
            { "accesscity-attempt", System.Text.Encoding.UTF8.GetBytes(attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)) },
            { "accesscity-event-type", System.Text.Encoding.UTF8.GetBytes(eventType) },
            { "accesscity-source-topic", System.Text.Encoding.UTF8.GetBytes(result.Topic) },
            { "accesscity-source-partition", System.Text.Encoding.UTF8.GetBytes(result.Partition.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)) },
            { "accesscity-source-offset", System.Text.Encoding.UTF8.GetBytes(result.Offset.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)) },
            { "accesscity-error", System.Text.Encoding.UTF8.GetBytes(error) }
        };

        if (deadLetter)
        {
            headers.Add("accesscity-dead-lettered-at", System.Text.Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToString("O")));
        }

        return new Message<string, string>
        {
            Key = result.Message.Key,
            Value = result.Message.Value,
            Headers = headers
        };
    }

    private static int ReadAttempt(Headers? headers)
    {
        if (headers is null)
        {
            return 1;
        }

        var header = headers.LastOrDefault(h => h.Key == "accesscity-attempt");
        if (header is null)
        {
            return 1;
        }

        var raw = System.Text.Encoding.UTF8.GetString(header.GetValueBytes());
        return int.TryParse(raw, out var attempt) && attempt > 0 ? attempt : 1;
    }

    private static string BuildMessageIdentity(string canonicalTopic, ConsumeResult<string, string> result)
    {
        var rawId = string.IsNullOrWhiteSpace(result.Message.Key)
            ? $"{result.Topic}:{result.Partition.Value}:{result.Offset.Value}"
            : result.Message.Key;

        return $"{canonicalTopic}:{rawId}";
    }

    private string TopicFor<T>() => _options.TopicPrefix + typeof(T).Name.ToLowerInvariant();
    private string RetryTopic(string topic) => topic + _options.RetryTopicSuffix;
    private string DeadLetterTopic(string topic) => topic + _options.DeadLetterTopicSuffix;

    private static JsonSerializerOptions CreateEventJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
        options.Converters.Add(new CoordinateJsonConverter());
        options.Converters.Add(new GeoJsonConverterFactory());
        return options;
    }
}
