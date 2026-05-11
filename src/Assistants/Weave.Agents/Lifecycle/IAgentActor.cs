using Weave.Agents.Chat;
using Weave.Agents.Verification;
using Weave.Shared.Ids;
using Weave.Workspaces.Manifest;
namespace Weave.Agents.Lifecycle;

public interface IAgentActor
{
    Task<AgentState> ActivateAgentAsync(WorkspaceId workspaceId, AgentDefinition definition);
    Task DeactivateAsync();
    Task<AgentState> GetStateAsync();
    Task<AgentChatResponse> SendAsync(AgentMessage message);
    IAsyncEnumerable<AgentChatStreamingFrame> SendStreamingAsync(AgentMessage message, CancellationToken ct = default);
    Task<AgentTaskInfo> SubmitTaskAsync(string description);
    Task CompleteTaskAsync(AgentTaskId taskId, bool success, ProofOfWork proof);
    Task ReviewTaskAsync(AgentTaskId taskId, bool accepted, string? feedback = null, VerificationRecord? verification = null);
    Task ConnectToolAsync(string toolName);
    Task DisconnectToolAsync(string toolName);
}
