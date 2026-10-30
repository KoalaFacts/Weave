namespace Weave.Agents.Verification;

public sealed record VerificationVote
{
    public required string ValidatorId { get; init; }
    public required bool Accepted { get; init; }
    public required string Reason { get; init; }
    public DateTimeOffset VotedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<ConditionResult> ConditionResults { get; init; } = [];
}
