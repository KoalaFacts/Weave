using Weave.Agents.Models;
using Weave.Workspaces.Models;

namespace Weave.Agents.Actors;

public interface IToolRegistryActor : IVirtualActorWithStringKey
{
    Task ConnectToolsAsync(Dictionary<string, ToolDefinition> tools);
    Task ConfigureAccessAsync(Dictionary<string, List<string>> agentToolAccess);
    Task GrantAgentToolsAsync(string agentName, IReadOnlyList<string> toolNames);
    Task DisconnectAllAsync();
    Task<ToolConnection?> GetConnectionAsync(string toolName);
    Task<IReadOnlyList<ToolConnection>> GetAllConnectionsAsync();
    Task<ToolResolution?> ResolveAsync(string agentName, string toolName);
}
