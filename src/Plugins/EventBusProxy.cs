using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Shared.Events;

namespace Weave.Shared.Plugins;

/// <summary>
/// Proxy <see cref="IEventBus"/> registered as the singleton in DI.
/// Owns the subscription list — when the backing bus is hot-swapped, all
/// active subscriptions are disposed on the old bus and re-created on the new one.
/// </summary>
/// <remarks>
/// Concurrent publishes hold an async-safe read lease. A swap waits for them
/// before moving subscriptions to the new bus.
/// </remarks>
public sealed partial class EventBusProxy : IEventBus, IDisposable
{
    private readonly PluginServiceBroker _broker;
    private readonly InProcessEventBus _fallback;
    private readonly ILogger<EventBusProxy> _logger;
    private readonly EventBusSwapGate _gate = new();
    private readonly Lock _lifetimeLock = new();
    private readonly IDisposable _swapRegistration;
    private readonly List<SubscriptionRecord> _subscriptions = [];
    private IEventBus _current;
    private int _disposed;

    public EventBusProxy(PluginServiceBroker broker, InProcessEventBus fallback, ILogger<EventBusProxy>? logger = null)
    {
        _broker = broker;
        _fallback = fallback;
        _logger = logger ?? NullLogger<EventBusProxy>.Instance;
        _current = fallback;
        _swapRegistration = _broker.OnSwap<IEventBus>(ReplaySubscriptions, bus => _current = bus ?? fallback);
    }

    public async Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct) where TEvent : IDomainEvent
    {
        using var lease = await _gate.EnterReadAsync(ct);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _current.PublishAsync(domainEvent, ct);
    }

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : IDomainEvent
    {
        using var lease = _gate.EnterWrite();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var innerSub = _current.Subscribe(handler);
        var record = new SubscriptionRecord(
            bus => bus.Subscribe(handler),
            innerSub);
        _subscriptions.Add(record);
        return new ProxySubscription(this, record);
    }

    private void Unsubscribe(SubscriptionRecord record)
    {
        lock (_lifetimeLock)
        {
            if (_disposed != 0)
                return;

            using var lease = _gate.EnterWrite();
            if (_subscriptions.Remove(record))
                record.InnerSubscription.Dispose();
        }
    }

    private void ReplaySubscriptions()
    {
        var bus = _broker.Get<IEventBus>() ?? _fallback;
        using var lease = _gate.EnterWrite();
        _current = bus;
        foreach (var record in _subscriptions)
        {
            var oldSub = record.InnerSubscription;
            try
            {
                record.InnerSubscription = record.SubscribeFactory(bus);
                oldSub.Dispose();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                record.InnerSubscription = oldSub;
                LogReplayFailed(ex);
            }
        }
    }

    public void Dispose()
    {
        lock (_lifetimeLock)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _swapRegistration.Dispose();
            using (var lease = _gate.EnterWrite())
            {
                foreach (var record in _subscriptions)
                    record.InnerSubscription.Dispose();
                _subscriptions.Clear();
            }
            _gate.Dispose();
        }
    }

    private sealed class SubscriptionRecord(
        Func<IEventBus, IDisposable> subscribeFactory,
        IDisposable innerSubscription)
    {
        public Func<IEventBus, IDisposable> SubscribeFactory { get; } = subscribeFactory;
        public IDisposable InnerSubscription { get; set; } = innerSubscription;
    }

    private sealed class ProxySubscription(EventBusProxy proxy, SubscriptionRecord record) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                proxy.Unsubscribe(record);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to replay event bus subscription after hot-swap; keeping previous subscription")]
    private partial void LogReplayFailed(Exception ex);
}
