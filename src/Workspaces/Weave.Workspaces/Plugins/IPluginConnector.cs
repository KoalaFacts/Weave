using Weave.Workspaces.Models;

namespace Weave.Workspaces.Plugins;

/// <summary>
/// Connects a plugin definition from the workspace manifest to runtime services.
/// Each connector handles one plugin <see cref="PluginDefinition.Type"/>.
/// </summary>
public interface IPluginConnector
{
    string PluginType { get; }
    PluginSchema Schema { get; }
    Task<PluginStatus> ConnectAsync(string name, PluginDefinition definition);
    Task<PluginStatus> DisconnectAsync(string name);
    PluginStatus GetStatus(string name);
}
