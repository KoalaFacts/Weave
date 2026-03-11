using Orleans;
using Weave.Shared.Events;

namespace Weave.Agents.Events;

[GenerateSerializer]
public sealed record ToolConnectedEvent : DomainEvent
{
    [Id(3)] public required string ToolName { get; init; }
    [Id(4)] public required string WorkspaceId { get; init; }
    [Id(5)] public required string ToolType { get; init; }
}

[GenerateSerializer]
public sealed record ToolDisconnectedEvent : DomainEvent
{
    [Id(3)] public required string ToolName { get; init; }
    [Id(4)] public required string WorkspaceId { get; init; }
}

[GenerateSerializer]
public sealed record ToolErrorEvent : DomainEvent
{
    [Id(3)] public required string ToolName { get; init; }
    [Id(4)] public required string WorkspaceId { get; init; }
    [Id(5)] public required string ErrorMessage { get; init; }
}
