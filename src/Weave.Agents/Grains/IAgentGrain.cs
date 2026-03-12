using Weave.Agents.Models;
using Weave.Shared.Ids;
using Weave.Workspaces.Models;

namespace Weave.Agents.Grains;

public interface IAgentGrain : IGrainWithStringKey
{
    Task<AgentState> ActivateAgentAsync(WorkspaceId workspaceId, AgentDefinition definition);
    Task DeactivateAsync();
    Task<AgentState> GetStateAsync();
    Task<AgentChatResponse> SendAsync(AgentMessage message);
    Task<AgentTaskInfo> SubmitTaskAsync(string description);
    Task CompleteTaskAsync(AgentTaskId taskId, bool success);
    Task ConnectToolAsync(string toolName);
    Task DisconnectToolAsync(string toolName);
}
