using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.ToolRegistry;

public sealed record ToolDisconnectedEvent : DomainEvent
{
    public required string ToolName { get; init; }
    public required WorkspaceId WorkspaceId { get; init; }
}
