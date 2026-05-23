using System.Collections.Concurrent;

namespace AccessCity.API.Messaging;

public class InMemoryMessageBus : IMessageBus
{
    private readonly ConcurrentDictionary<Type, List<Func<IntegrationEvent, Task>>> _handlers = new();
    private readonly ConcurrentDictionary<Type, Queue<IntegrationEvent>> _pending = new();
    private readonly object _gate = new();

    public async Task PublishAsync<T>(T @event, CancellationToken cancellationToken = default) where T : IntegrationEvent
    {
        List<Func<IntegrationEvent, Task>> handlers;
        lock (_gate)
        {
            if (!_handlers.TryGetValue(typeof(T), out var registered) || registered.Count == 0)
            {
                var queue = _pending.GetOrAdd(typeof(T), _ => new Queue<IntegrationEvent>());
                queue.Enqueue(@event);
                return;
            }

            handlers = registered.ToList();
        }

        foreach (var handler in handlers)
        {
            _ = Task.Run(() => handler(@event), cancellationToken);
        }

        await Task.CompletedTask;
    }

    public Task SubscribeAsync<T>(Func<T, Task> handler, CancellationToken cancellationToken = default) where T : IntegrationEvent
    {
        List<IntegrationEvent> replay;
        lock (_gate)
        {
            var handlers = _handlers.GetOrAdd(typeof(T), _ => new List<Func<IntegrationEvent, Task>>());
            handlers.Add(e => handler((T)e));

            replay = new List<IntegrationEvent>();
            if (_pending.TryGetValue(typeof(T), out var queue))
            {
                while (queue.Count > 0)
                {
                    replay.Add(queue.Dequeue());
                }
            }
        }

        foreach (var @event in replay)
        {
            _ = Task.Run(() => handler((T)@event), cancellationToken);
        }

        return Task.CompletedTask;
    }
}
