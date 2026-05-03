using Weave.Agents.Models;
using Weave.Shared.Ids;

namespace Weave.Agents.Verification;

public interface IAgentVerificationDispatcher
{
    ValueTask EnqueueAsync(AgentVerificationRequest request, CancellationToken ct);
}

public sealed record AgentVerificationRequest(
    WorkspaceId WorkspaceId,
    string AgentName,
    AgentTaskId TaskId,
    ProofOfWork Proof);
