using Orleans;
using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Tools.Events;

[GenerateSerializer]
public sealed record ToolInvocationCompletedEvent : DomainEvent
{
    [Id(3)] public required string ToolName { get; init; }
    [Id(4)] public required WorkspaceId WorkspaceId { get; init; }
    [Id(5)] public required bool Success { get; init; }
    [Id(6)] public required TimeSpan Duration { get; init; }
}

[GenerateSerializer]
public sealed record ToolInvocationBlockedEvent : DomainEvent
{
    [Id(3)] public required string ToolName { get; init; }
    [Id(4)] public required WorkspaceId WorkspaceId { get; init; }
    [Id(5)] public required string Reason { get; init; }
}
