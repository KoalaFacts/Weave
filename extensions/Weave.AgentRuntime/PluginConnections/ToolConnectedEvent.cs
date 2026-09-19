using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.ToolRegistry;

public sealed record ToolConnectedEvent : DomainEvent
{
    public required string ToolName { get; init; }
    public required WorkspaceId WorkspaceId { get; init; }
    public required string ToolType { get; init; }
}
