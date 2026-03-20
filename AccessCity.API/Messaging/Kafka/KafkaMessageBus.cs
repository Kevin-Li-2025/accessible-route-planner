using Confluent.Kafka;
using System.Text.Json;

namespace AccessCity.API.Messaging.Kafka;

public class KafkaMessageBus : IMessageBus
{
    private readonly IProducer<string, string> _producer;
    private readonly string _topicPrefix;

    public KafkaMessageBus(IConfiguration configuration)
    {
        var config = new ProducerConfig
        {
            BootstrapServers = configuration["Kafka:BootstrapServers"] ?? "localhost:9092",
            ClientId = "AccessCity.API"
        };
        _producer = new ProducerBuilder<string, string>(config).Build();
        _topicPrefix = configuration["Kafka:TopicPrefix"] ?? "accesscity_";
    }

    public async Task PublishAsync<T>(T @event, CancellationToken cancellationToken = default) where T : IntegrationEvent
    {
        var topic = _topicPrefix + typeof(T).Name.ToLower();
        var message = new Message<string, string>
        {
            Key = @event.Id.ToString(),
            Value = JsonSerializer.Serialize(@event)
        };

        await _producer.ProduceAsync(topic, message, cancellationToken);
    }

    public Task SubscribeAsync<T>(Func<T, Task> handler, CancellationToken cancellationToken = default) where T : IntegrationEvent
    {
        throw new NotImplementedException("Use a BackgroundService for Kafka consumers.");
    }
}
