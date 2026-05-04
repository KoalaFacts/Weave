using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Verification;

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
