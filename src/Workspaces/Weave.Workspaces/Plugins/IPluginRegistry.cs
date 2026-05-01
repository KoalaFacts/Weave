using Weave.Workspaces.Models;

namespace Weave.Workspaces.Plugins;

/// <summary>
/// Manages plugin lifecycle — connects, disconnects, and hot-swaps plugins.
/// Validates config against the connector's <see cref="PluginSchema"/> and
/// auto-fills missing values from environment variables before connecting.
/// </summary>
public interface IPluginRegistry
{
    Task<IReadOnlyList<PluginStatus>> ConnectAllAsync(Dictionary<string, PluginDefinition> plugins);
    Task<PluginStatus> ConnectAsync(string name, PluginDefinition definition);
    Task<PluginStatus> DisconnectAsync(string name);
    IReadOnlyList<PluginStatus> GetAll();
    IReadOnlyList<PluginSchema> GetCatalog();
}
