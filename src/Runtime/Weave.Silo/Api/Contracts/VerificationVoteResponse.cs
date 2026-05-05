using Weave.Agents.Verification;
namespace Weave.Silo.Api;

public sealed record VerificationVoteResponse
{
    public required string ValidatorId { get; init; }
    public required bool Accepted { get; init; }
    public required string Reason { get; init; }
    public required DateTimeOffset VotedAt { get; init; }
    public List<ConditionResultResponse> ConditionResults { get; init; } = [];

    public static VerificationVoteResponse FromVote(VerificationVote vote) => new()
    {
        ValidatorId = vote.ValidatorId,
        Accepted = vote.Accepted,
        Reason = vote.Reason,
        VotedAt = vote.VotedAt,
        ConditionResults = vote.ConditionResults.Select(ConditionResultResponse.FromResult).ToList()
    };
}
