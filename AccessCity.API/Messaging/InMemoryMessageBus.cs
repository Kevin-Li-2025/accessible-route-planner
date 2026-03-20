using System.Collections.Concurrent;

namespace AccessCity.API.Messaging;

public class InMemoryMessageBus : IMessageBus
{
    private readonly ConcurrentDictionary<Type, List<Func<IntegrationEvent, Task>>> _handlers = new();

    public async Task PublishAsync<T>(T @event, CancellationToken cancellationToken = default) where T : IntegrationEvent
    {
        if (_handlers.TryGetValue(typeof(T), out var handlers))
        {
            foreach (var handler in handlers)
            {
                _ = Task.Run(() => handler(@event), cancellationToken);
            }
        }
        await Task.CompletedTask;
    }

    public Task SubscribeAsync<T>(Func<T, Task> handler, CancellationToken cancellationToken = default) where T : IntegrationEvent
    {
        var handlers = _handlers.GetOrAdd(typeof(T), _ => new List<Func<IntegrationEvent, Task>>());
        handlers.Add(e => handler((T)e));
        return Task.CompletedTask;
    }
}
