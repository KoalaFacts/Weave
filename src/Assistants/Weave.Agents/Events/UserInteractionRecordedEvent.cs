using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Events;

public sealed record UserInteractionRecordedEvent : DomainEvent
{
    public required WorkspaceId WorkspaceId { get; init; }
    public required string UserId { get; init; }
    public required string AgentName { get; init; }
}
