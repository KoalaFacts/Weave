using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Tools.Events;

public sealed record ToolInvocationBlockedEvent : DomainEvent
{
    public required string ToolName { get; init; }
    public required WorkspaceId WorkspaceId { get; init; }
    public required string Reason { get; init; }
}
