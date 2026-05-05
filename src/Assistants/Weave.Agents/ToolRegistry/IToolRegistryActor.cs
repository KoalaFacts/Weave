using Weave.Workspaces.Manifest;
namespace Weave.Agents.ToolRegistry;

public interface IToolRegistryActor
{
    Task ConnectToolsAsync(Dictionary<string, ToolDefinition> tools);
    Task ConfigureAccessAsync(Dictionary<string, List<string>> agentToolAccess);
    Task GrantAgentToolsAsync(string agentName, IReadOnlyList<string> toolNames);
    Task DisconnectAllAsync();
    Task<ToolConnection?> GetConnectionAsync(string toolName);
    Task<IReadOnlyList<ToolConnection>> GetAllConnectionsAsync();
    Task<ToolResolution?> ResolveAsync(string agentName, string toolName);
}
