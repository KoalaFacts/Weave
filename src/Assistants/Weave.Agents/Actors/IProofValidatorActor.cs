using Weave.Agents.Models;

namespace Weave.Agents.Actors;

public interface IProofValidatorActor
{
    Task<VerificationVote> ValidateAsync(string validatorId, ProofOfWork proof, List<VerificationCondition> conditions, string? modelId = null);
}
