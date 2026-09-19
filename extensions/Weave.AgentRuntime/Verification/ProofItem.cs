namespace Weave.Agents.Verification;

public sealed record ProofItem
{
    public required ProofType Type { get; init; }
    public required string Label { get; init; }
    public required string Value { get; init; }
    public string? Uri { get; init; }
}
