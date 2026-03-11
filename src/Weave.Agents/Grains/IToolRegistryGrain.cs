using Orleans;
using Weave.Agents.Models;
using Weave.Workspaces.Models;

namespace Weave.Agents.Grains;

public interface IToolRegistryGrain : IGrainWithStringKey
{
    Task ConnectToolsAsync(Dictionary<string, ToolDefinition> tools);
    Task DisconnectAllAsync();
    Task<ToolConnection?> GetConnectionAsync(string toolName);
    Task<IReadOnlyList<ToolConnection>> GetAllConnectionsAsync();
}
