namespace Weave.Agents.Verification;

public interface IProofValidatorActor
{
    Task<VerificationVote> ValidateAsync(string validatorId, ProofOfWork proof, List<VerificationCondition> conditions, string? modelId = null);
}
