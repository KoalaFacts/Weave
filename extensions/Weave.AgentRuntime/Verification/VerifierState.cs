namespace Weave.Agents.Verification;

public sealed record VerifierState
{
    public List<VerificationCondition> Conditions { get; init; } = [];
    public int RequiredValidators { get; set; } = 2;
    public List<ValidatorConfig> ValidatorConfigs { get; init; } = [];
}
