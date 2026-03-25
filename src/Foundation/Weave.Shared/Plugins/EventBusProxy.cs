using Weave.Shared.Events;

namespace Weave.Shared.Plugins;

/// <summary>
/// Proxy <see cref="IEventBus"/> registered as the singleton in DI.
/// Delegates to the broker's current event bus if a plugin has swapped it in,
/// otherwise falls back to <see cref="InProcessEventBus"/>.
/// Grains and services inject this without knowing which implementation backs it.
/// </summary>
public sealed class EventBusProxy(PluginServiceBroker broker, InProcessEventBus fallback) : IEventBus
{
    private IEventBus Current => broker.Get<IEventBus>() ?? fallback;

    public Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct) where TEvent : IDomainEvent
        => Current.PublishAsync(domainEvent, ct);

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : IDomainEvent
        => Current.Subscribe(handler);
}
