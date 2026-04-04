using Weave.Shared.Events;

namespace Weave.Shared.Plugins;

/// <summary>
/// Proxy <see cref="IEventBus"/> registered as the singleton in DI.
/// Owns the subscription list — when the backing bus is hot-swapped, all active
/// subscriptions are disposed on the old bus and re-created on the new one.
///
/// Uses <see cref="ReaderWriterLockSlim"/>: publishes take a read lock (concurrent),
/// swaps take a write lock (exclusive, waits for in-flight publishes to drain).
/// </summary>
public sealed class EventBusProxy : IEventBus
{
    private readonly PluginServiceBroker _broker;
    private readonly InProcessEventBus _fallback;
    private readonly ReaderWriterLockSlim _rwLock = new();
    private readonly List<SubscriptionRecord> _subscriptions = [];

    public EventBusProxy(PluginServiceBroker broker, InProcessEventBus fallback)
    {
        _broker = broker;
        _fallback = fallback;
        _broker.OnSwap<IEventBus>(ReplaySubscriptions);
    }

    private IEventBus Current => _broker.Get<IEventBus>() ?? _fallback;

    public async Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct) where TEvent : IDomainEvent
    {
        // Read lock: multiple publishes proceed concurrently.
        // A swap (write lock) blocks until all in-flight publishes complete.
        _rwLock.EnterReadLock();
        try
        {
            var bus = Current;
            await bus.PublishAsync(domainEvent, ct);
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : IDomainEvent
    {
        _rwLock.EnterWriteLock();
        try
        {
            var innerSub = Current.Subscribe(handler);
            var record = new SubscriptionRecord(
                bus => bus.Subscribe(handler),
                innerSub);
            _subscriptions.Add(record);
            return new ProxySubscription(this, record);
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    private void Unsubscribe(SubscriptionRecord record)
    {
        _rwLock.EnterWriteLock();
        try
        {
            if (_subscriptions.Remove(record))
                record.InnerSubscription.Dispose();
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    private void ReplaySubscriptions()
    {
        // Called from broker.Swap callback which holds broker._lock.
        // We take the write lock here — this blocks until all in-flight
        // publishes (read locks) drain, guaranteeing no publish can
        // target a bus whose subscriptions have been replayed away.
        _rwLock.EnterWriteLock();
        try
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
                catch (Exception)
                {
                    // If re-subscribe fails, keep the old subscription reference
                    // so that Unsubscribe can still dispose it cleanly.
                    record.InnerSubscription = oldSub;
                }
            }
        }
        finally
        {
            _rwLock.ExitWriteLock();
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
