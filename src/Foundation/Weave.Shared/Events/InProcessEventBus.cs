using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Weave.Shared.Events;

public sealed partial class InProcessEventBus(ILogger<InProcessEventBus> logger) : IEventBus
{
    private readonly ConcurrentDictionary<Type, List<Delegate>> _handlers = new();

    public async Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct) where TEvent : IDomainEvent
    {
        if (!_handlers.TryGetValue(typeof(TEvent), out var handlers))
            return;

        // Snapshot under lock to avoid race with concurrent Subscribe/Unsubscribe
        Delegate[] snapshot;
        lock (handlers)
        { snapshot = [.. handlers]; }
        foreach (var handler in snapshot)
        {
            try
            {
                await ((Func<TEvent, CancellationToken, Task>)handler)(domainEvent, ct);
            }
            catch (Exception ex)
            {
                LogEventHandlerError(ex, typeof(TEvent).Name, domainEvent.EventId);
            }
        }
    }

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : IDomainEvent
    {
        var handlers = _handlers.GetOrAdd(typeof(TEvent), _ => []);
        lock (handlers)
        {
            handlers.Add(handler);
        }
        return new Subscription(() =>
        {
            lock (handlers)
            {
                handlers.Remove(handler);
            }
        });
    }

    private sealed class Subscription(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Error handling event {EventType} ({EventId})")]
    private partial void LogEventHandlerError(Exception ex, string eventType, string eventId);
}
