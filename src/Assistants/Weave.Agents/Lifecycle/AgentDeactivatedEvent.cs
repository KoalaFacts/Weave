using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Lifecycle;

public sealed record AgentDeactivatedEvent : DomainEvent
{
    public required string AgentName { get; init; }
    public required WorkspaceId WorkspaceId { get; init; }
}
