using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Events;

public sealed record AgentActivatedEvent : DomainEvent
{
    public required string AgentName { get; init; }
    public required WorkspaceId WorkspaceId { get; init; }
    public required string Model { get; init; }
    public List<string> Tools { get; init; } = [];
}
public sealed record AgentDeactivatedEvent : DomainEvent
{
    public required string AgentName { get; init; }
    public required WorkspaceId WorkspaceId { get; init; }
}
public sealed record AgentErrorEvent : DomainEvent
{
    public required string AgentName { get; init; }
    public required WorkspaceId WorkspaceId { get; init; }
    public required string ErrorMessage { get; init; }
}
public sealed record AgentTaskCompletedEvent : DomainEvent
{
    public required string AgentName { get; init; }
    public required WorkspaceId WorkspaceId { get; init; }
    public required AgentTaskId TaskId { get; init; }
}
public sealed record AgentTaskAwaitingReviewEvent : DomainEvent
{
    public required string AgentName { get; init; }
    public required WorkspaceId WorkspaceId { get; init; }
    public required AgentTaskId TaskId { get; init; }
}
public sealed record AgentTaskReviewedEvent : DomainEvent
{
    public required string AgentName { get; init; }
    public required WorkspaceId WorkspaceId { get; init; }
    public required AgentTaskId TaskId { get; init; }
    public required bool Accepted { get; init; }
}
public sealed record ProofVerifiedEvent : DomainEvent
{
    public required WorkspaceId WorkspaceId { get; init; }
    public required string AgentName { get; init; }
    public required AgentTaskId TaskId { get; init; }
    public required bool Accepted { get; init; }
    public required string Feedback { get; init; }
    public required int VoteCount { get; init; }
    public required int AcceptCount { get; init; }
}
public sealed record SkillCreatedEvent : DomainEvent
{
    public required WorkspaceId WorkspaceId { get; init; }
    public required SkillId SkillId { get; init; }
    public required string Title { get; init; }
    public required string CreatedByAgent { get; init; }
}
