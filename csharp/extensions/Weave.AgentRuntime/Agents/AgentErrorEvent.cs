using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Lifecycle;

public sealed record AgentErrorEvent : DomainEvent
{
    public required string AgentName { get; init; }
    public required WorkspaceId WorkspaceId { get; init; }
    public required string ErrorMessage { get; init; }
}
