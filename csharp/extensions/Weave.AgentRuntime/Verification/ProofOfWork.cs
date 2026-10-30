namespace Weave.Agents.Verification;

public sealed record ProofOfWork
{
    public List<ProofItem> Items { get; init; } = [];
    public DateTimeOffset SubmittedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? ReviewFeedback { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public VerificationRecord? Verification { get; set; }
}
