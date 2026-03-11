using Orleans;
using Weave.Agents.Models;
using Weave.Workspaces.Models;

namespace Weave.Agents.Grains;

public interface IAgentGrain : IGrainWithStringKey
{
    Task<AgentState> ActivateAgentAsync(string workspaceId, AgentDefinition definition);
    Task DeactivateAsync();
    Task<AgentState> GetStateAsync();
    Task<AgentTaskInfo> SubmitTaskAsync(string description);
    Task CompleteTaskAsync(string taskId, bool success);
    Task ConnectToolAsync(string toolName);
    Task DisconnectToolAsync(string toolName);
}
