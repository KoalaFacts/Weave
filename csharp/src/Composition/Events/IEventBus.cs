namespace Weave.Shared.Events;

public interface IEventBus
{
    Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct) where TEvent : IDomainEvent;
    IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : IDomainEvent;
}
