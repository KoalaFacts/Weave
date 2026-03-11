using System.Collections.Concurrent;
using Dapr.Client;
using Microsoft.Extensions.Logging;
using Weave.Shared.Events;

namespace Weave.Silo.Events;

/// <summary>
/// Event bus that publishes domain events to Dapr pub/sub while also
/// dispatching to local in-process subscribers for grain-to-grain communication.
/// </summary>
public sealed class DaprEventBus(
    DaprClient daprClient,
    ILogger<DaprEventBus> logger) : IEventBus
{
    private const string PubSubName = "pubsub";
    private readonly ConcurrentDictionary<Type, List<Delegate>> _handlers = new();

    public async Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct) where TEvent : IDomainEvent
    {
        var topicName = typeof(TEvent).Name;

        // Publish to Dapr pub/sub for cross-silo distribution
        try
        {
            await daprClient.PublishEventAsync(PubSubName, topicName, domainEvent, ct);
            logger.LogDebug("Published {EventType} ({EventId}) to Dapr topic {Topic}",
                topicName, domainEvent.EventId, topicName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish {EventType} to Dapr, falling back to local-only", topicName);
        }

        // Also dispatch locally for in-process subscribers
        await DispatchLocalAsync(domainEvent, ct);
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

    private async Task DispatchLocalAsync<TEvent>(TEvent domainEvent, CancellationToken ct) where TEvent : IDomainEvent
    {
        if (!_handlers.TryGetValue(typeof(TEvent), out var handlers))
            return;

        var snapshot = handlers.ToArray();
        foreach (var handler in snapshot)
        {
            try
            {
                await ((Func<TEvent, CancellationToken, Task>)handler)(domainEvent, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error handling event {EventType} ({EventId})",
                    typeof(TEvent).Name, domainEvent.EventId);
            }
        }
    }

    private sealed class Subscription(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
