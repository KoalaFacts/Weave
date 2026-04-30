namespace Weave.Agents.Models;

public sealed record VerificationRecord
{
    public List<VerificationVote> Votes { get; init; } = [];
    public required int RequiredVotes { get; init; }
    public required bool ConsensusReached { get; init; }
    public required bool Accepted { get; init; }
    public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.UtcNow;
}