namespace Weave.Shared.Events;

[GenerateSerializer]
public abstract record DomainEvent : IDomainEvent
{
    [Id(0)] public string EventId { get; init; } = Guid.NewGuid().ToString("N");
    [Id(1)] public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    [Id(2)] public required string SourceId { get; init; }
}
