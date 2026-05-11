using Weave.Agents.Verification;
namespace Weave.Silo.Api;

public sealed record ProofOfWorkResponse
{
    public List<ProofItemResponse> Items { get; init; } = [];
    public required DateTimeOffset SubmittedAt { get; init; }
    public string? ReviewFeedback { get; init; }
    public DateTimeOffset? ReviewedAt { get; init; }
    public VerificationRecordResponse? Verification { get; init; }

    public static ProofOfWorkResponse FromProof(ProofOfWork proof) => new()
    {
        Items = proof.Items.Select(ProofItemResponse.FromItem).ToList(),
        SubmittedAt = proof.SubmittedAt,
        ReviewFeedback = proof.ReviewFeedback,
        ReviewedAt = proof.ReviewedAt,
        Verification = proof.Verification is not null
            ? VerificationRecordResponse.FromRecord(proof.Verification)
            : null
    };
}
