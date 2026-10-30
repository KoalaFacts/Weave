namespace Weave.Shared.Events;

/// <summary>
/// Base for all domain events. Orleans serialization is applied
/// externally via surrogates in <c>Weave.Shared.Orleans</c> so this
/// assembly stays Orleans-free.
/// </summary>
public abstract record DomainEvent : IDomainEvent
{
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public required string SourceId { get; init; }
}
