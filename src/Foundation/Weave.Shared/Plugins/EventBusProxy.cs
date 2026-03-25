using Weave.Shared.Events;

namespace Weave.Shared.Plugins;

/// <summary>
/// Proxy <see cref="IEventBus"/> registered as the singleton in DI.
/// Owns the subscription list — when the backing bus is hot-swapped, all active
/// subscriptions are disposed on the old bus and re-created on the new one.
/// Consumers never need to know a swap happened.
/// </summary>
public sealed class EventBusProxy : IEventBus
{
    private readonly PluginServiceBroker _broker;
    private readonly InProcessEventBus _fallback;
    private readonly Lock _lock = new();
    private readonly List<SubscriptionRecord> _subscriptions = [];

    public EventBusProxy(PluginServiceBroker broker, InProcessEventBus fallback)
    {
        _broker = broker;
        _fallback = fallback;
        _broker.OnSwap<IEventBus>(ReplaySubscriptions);
    }

    private IEventBus Current => _broker.Get<IEventBus>() ?? _fallback;

    public Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct) where TEvent : IDomainEvent
    {
        // Snapshot the current bus reference under the lock to ensure we publish
        // to the same bus that holds our subscriptions. Without this, a concurrent
        // swap could cause us to publish to the old bus after subscriptions have
        // been replayed to the new one.
        IEventBus bus;
        lock (_lock) { bus = Current; }
        return bus.PublishAsync(domainEvent, ct);
    }

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : IDomainEvent
    {
        lock (_lock)
        {
            var innerSub = Current.Subscribe(handler);
            var record = new SubscriptionRecord(
                bus => bus.Subscribe(handler),
                innerSub);
            _subscriptions.Add(record);
            return new ProxySubscription(this, record);
        }
    }

    private void Unsubscribe(SubscriptionRecord record)
    {
        lock (_lock)
        {
            if (_subscriptions.Remove(record))
                record.InnerSubscription.Dispose();
        }
    }

    private void ReplaySubscriptions()
    {
        lock (_lock)
        {
            var bus = Current;
            foreach (var record in _subscriptions)
            {
                var oldSub = record.InnerSubscription;
                try
                {
                    record.InnerSubscription = record.SubscribeFactory(bus);
                    oldSub.Dispose();
                }
                catch
                {
                    // If re-subscribe fails, keep the old subscription reference
                    // so that Unsubscribe can still dispose it cleanly.
                    record.InnerSubscription = oldSub;
                }
            }
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
}
