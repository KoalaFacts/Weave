using Weave.Agents.Verification;
namespace Weave.Silo.Api;

public sealed record VerificationRecordResponse
{
    public List<VerificationVoteResponse> Votes { get; init; } = [];
    public required int RequiredVotes { get; init; }
    public required bool ConsensusReached { get; init; }
    public required bool Accepted { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }

    public static VerificationRecordResponse FromRecord(VerificationRecord record) => new()
    {
        Votes = record.Votes.Select(VerificationVoteResponse.FromVote).ToList(),
        RequiredVotes = record.RequiredVotes,
        ConsensusReached = record.ConsensusReached,
        Accepted = record.Accepted,
        CompletedAt = record.CompletedAt
    };
}
