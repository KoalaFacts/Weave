using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Lifecycle;

public sealed record AgentActivatedEvent : DomainEvent
{
    public required string AgentName { get; init; }
    public required WorkspaceId WorkspaceId { get; init; }
    public required string Model { get; init; }
    public IReadOnlyList<string> Tools { get; init; } = [];
}
