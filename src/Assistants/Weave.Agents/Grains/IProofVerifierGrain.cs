using Weave.Agents.Models;
using Weave.Shared.Ids;

namespace Weave.Agents.Grains;

public interface IProofVerifierGrain : IGrainWithStringKey
{
    Task VerifyAsync(WorkspaceId workspaceId, string agentName, AgentTaskId taskId, ProofOfWork proof);
    Task ConfigureAsync(List<VerificationCondition> conditions, int requiredValidators, List<ValidatorConfig>? validatorConfigs = null);
    Task<List<VerificationCondition>> GetConditionsAsync();
}
