namespace AccessCity.API.Messaging;

public abstract record IntegrationEvent
{
    public Guid Id { get; } = Guid.NewGuid();
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
}

public interface IKeyedIntegrationEvent
{
    string PartitionKey { get; }
}

public interface IMessageBus
{
    Task PublishAsync<T>(T @event, CancellationToken cancellationToken = default) where T : IntegrationEvent;
    Task SubscribeAsync<T>(Func<T, Task> handler, CancellationToken cancellationToken = default) where T : IntegrationEvent;
}
