using Orleans;
using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Events;

[GenerateSerializer]
public sealed record AgentActivatedEvent : DomainEvent
{
    [Id(3)] public required string AgentName { get; init; }
    [Id(4)] public required WorkspaceId WorkspaceId { get; init; }
    [Id(5)] public required string Model { get; init; }
    [Id(6)] public List<string> Tools { get; init; } = [];
}

[GenerateSerializer]
public sealed record AgentDeactivatedEvent : DomainEvent
{
    [Id(3)] public required string AgentName { get; init; }
    [Id(4)] public required WorkspaceId WorkspaceId { get; init; }
}

[GenerateSerializer]
public sealed record AgentErrorEvent : DomainEvent
{
    [Id(3)] public required string AgentName { get; init; }
    [Id(4)] public required WorkspaceId WorkspaceId { get; init; }
    [Id(5)] public required string ErrorMessage { get; init; }
}

[GenerateSerializer]
public sealed record AgentTaskCompletedEvent : DomainEvent
{
    [Id(3)] public required string AgentName { get; init; }
    [Id(4)] public required WorkspaceId WorkspaceId { get; init; }
    [Id(5)] public required AgentTaskId TaskId { get; init; }
}
