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

[GenerateSerializer]
public sealed record AgentTaskAwaitingReviewEvent : DomainEvent
{
    [Id(3)] public required string AgentName { get; init; }
    [Id(4)] public required WorkspaceId WorkspaceId { get; init; }
    [Id(5)] public required AgentTaskId TaskId { get; init; }
}

[GenerateSerializer]
public sealed record AgentTaskReviewedEvent : DomainEvent
{
    [Id(3)] public required string AgentName { get; init; }
    [Id(4)] public required WorkspaceId WorkspaceId { get; init; }
    [Id(5)] public required AgentTaskId TaskId { get; init; }
    [Id(6)] public required bool Accepted { get; init; }
}

[GenerateSerializer]
public sealed record ProofVerifiedEvent : DomainEvent
{
    [Id(3)] public required WorkspaceId WorkspaceId { get; init; }
    [Id(4)] public required string AgentName { get; init; }
    [Id(5)] public required AgentTaskId TaskId { get; init; }
    [Id(6)] public required bool Accepted { get; init; }
    [Id(7)] public required string Feedback { get; init; }
    [Id(8)] public required int VoteCount { get; init; }
    [Id(9)] public required int AcceptCount { get; init; }
}
