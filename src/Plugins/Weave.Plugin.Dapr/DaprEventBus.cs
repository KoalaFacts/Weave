using System.Collections.Concurrent;
using Dapr.Client;
using Microsoft.Extensions.Logging;
using Weave.Shared.Events;

namespace Weave.Plugin.Dapr;

/// <summary>
/// Event bus that publishes domain events to Dapr pub/sub while also
/// dispatching to local in-process subscribers for grain-to-grain communication.
/// </summary>
public sealed partial class DaprEventBus(
    DaprClient daprClient,
    ILogger<DaprEventBus> logger) : IEventBus
{
    private const string PubSubName = "pubsub";
    private readonly ConcurrentDictionary<Type, List<Delegate>> _handlers = new();

    public async Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct) where TEvent : IDomainEvent
    {
        var topicName = typeof(TEvent).Name;

        try
        {
            await daprClient.PublishEventAsync(PubSubName, topicName, domainEvent, ct);
            LogEventPublished(topicName, domainEvent.EventId, topicName);
        }
        catch (Exception ex)
        {
            LogDaprPublishFailed(ex, topicName);
        }

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
                LogEventHandlerError(ex, typeof(TEvent).Name, domainEvent.EventId);
            }
        }
    }

    private sealed class Subscription(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Published {EventType} ({EventId}) to Dapr topic {Topic}")]
    private partial void LogEventPublished(string eventType, string eventId, string topic);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to publish {EventType} to Dapr, falling back to local-only")]
    private partial void LogDaprPublishFailed(Exception ex, string eventType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error handling event {EventType} ({EventId})")]
    private partial void LogEventHandlerError(Exception ex, string eventType, string eventId);
}
