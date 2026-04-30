using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Events;

public sealed record AgentTaskReviewedEvent : DomainEvent
{
    public required string AgentName { get; init; }
    public required WorkspaceId WorkspaceId { get; init; }
    public required AgentTaskId TaskId { get; init; }
    public required bool Accepted { get; init; }
}
