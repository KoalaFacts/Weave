using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Tools.Tool;

public sealed record ToolInvocationCompletedEvent : DomainEvent
{
    public required string ToolName { get; init; }
    public required WorkspaceId WorkspaceId { get; init; }
    public required bool Success { get; init; }
    public required TimeSpan Duration { get; init; }
}
