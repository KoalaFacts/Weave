namespace Weave.Agents.Verification;

public sealed record VerificationCondition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
}
