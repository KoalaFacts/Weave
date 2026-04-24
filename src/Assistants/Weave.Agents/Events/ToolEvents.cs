using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Events;
public sealed record ToolConnectedEvent : DomainEvent
{
    public required string ToolName { get; init; }
    public required WorkspaceId WorkspaceId { get; init; }
    public required string ToolType { get; init; }
}
public sealed record ToolDisconnectedEvent : DomainEvent
{
    public required string ToolName { get; init; }
    public required WorkspaceId WorkspaceId { get; init; }
}
public sealed record ToolErrorEvent : DomainEvent
{
    public required string ToolName { get; init; }
    public required WorkspaceId WorkspaceId { get; init; }
    public required string ErrorMessage { get; init; }
}
