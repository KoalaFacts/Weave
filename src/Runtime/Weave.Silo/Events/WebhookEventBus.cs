using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Weave.Shared.Events;

namespace Weave.Silo.Events;

/// <summary>
/// HTTP webhook event bus — publishes domain events to configured webhook URLs
/// via plain HTTP POST. No sidecar, no message broker, no external infrastructure.
/// A lightweight alternative to <see cref="DaprEventBus"/> for local and simple deployments.
///
/// The event type name is sent in the <c>X-Weave-Topic</c> header; the body contains
/// the event serialized as UTF-8 JSON. This avoids an envelope type that would require
/// reflection-based serialization.
/// </summary>
public sealed partial class WebhookEventBus(
    HttpClient httpClient,
    Uri webhookUrl,
    ILogger<WebhookEventBus> logger) : IEventBus
{
    private readonly ConcurrentDictionary<Type, List<Delegate>> _handlers = new();

    public async Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct) where TEvent : IDomainEvent
    {
        var topicName = typeof(TEvent).Name;
        try
        {
            // Serialize the concrete TEvent — STJ can resolve the type at compile time
            // without requiring a source-gen context for every event type, because the
            // generic parameter provides the actual type to the serializer.
            var bytes = JsonSerializer.SerializeToUtf8Bytes(domainEvent);
            using var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            content.Headers.Add("X-Weave-Topic", topicName);
            using var response = await httpClient.PostAsync(webhookUrl, content, ct);
            response.EnsureSuccessStatusCode();
            LogWebhookPublished(topicName, domainEvent.EventId);
        }
        catch (Exception ex)
        {
            LogWebhookPublishFailed(ex, topicName);
        }

        await DispatchLocalAsync(domainEvent, ct);
    }

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : IDomainEvent
    {
        var handlers = _handlers.GetOrAdd(typeof(TEvent), _ => []);
        lock (handlers)
        { handlers.Add(handler); }
        return new Subscription(() => { lock (handlers) { handlers.Remove(handler); } });
    }

    private async Task DispatchLocalAsync<TEvent>(TEvent domainEvent, CancellationToken ct) where TEvent : IDomainEvent
    {
        if (!_handlers.TryGetValue(typeof(TEvent), out var handlers))
            return;
        Delegate[] snapshot;
        lock (handlers)
        { snapshot = [.. handlers]; }
        foreach (var handler in snapshot)
        {
            try
            { await ((Func<TEvent, CancellationToken, Task>)handler)(domainEvent, ct); }
            catch (Exception ex) { LogEventHandlerError(ex, typeof(TEvent).Name, domainEvent.EventId); }
        }
    }

    private sealed class Subscription(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Published {EventType} ({EventId}) via webhook")]
    private partial void LogWebhookPublished(string eventType, string eventId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to publish {EventType} via webhook, falling back to local-only")]
    private partial void LogWebhookPublishFailed(Exception ex, string eventType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error handling event {EventType} ({EventId})")]
    private partial void LogEventHandlerError(Exception ex, string eventType, string eventId);
}
