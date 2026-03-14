using Weave.Agents.Models;

namespace Weave.Agents.Grains;

public interface IProofValidatorGrain : IGrainWithStringKey
{
    Task<VerificationVote> ValidateAsync(string validatorId, ProofOfWork proof, List<VerificationCondition> conditions, string? modelId = null);
}
