namespace Weave.Shared.Events;

public interface IDomainEvent
{
    string EventId { get; }
    DateTimeOffset Timestamp { get; }
    string SourceId { get; }
}
