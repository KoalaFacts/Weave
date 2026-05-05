using Weave.Security.Tokens;
using Weave.Workspaces.Manifest;
namespace Weave.Silo.Plugins;

/// <summary>
/// Manages plugin lifecycle — connects, disconnects, and hot-swaps plugins.
/// Validates config against the connector's <see cref="PluginSchema"/> and
/// auto-fills missing values from environment variables before connecting.
/// Mutations require a <see cref="CapabilityToken"/> with grant
/// <c>plugin:invoke:{name}</c>.
/// </summary>
public interface IPluginRegistry
{
    Task<IReadOnlyList<PluginStatus>> ConnectAllAsync(Dictionary<string, PluginDefinition> plugins, CapabilityToken token);
    Task<PluginStatus> ConnectAsync(string name, PluginDefinition definition, CapabilityToken token);
    Task<PluginStatus> DisconnectAsync(string name, CapabilityToken token);
    IReadOnlyList<PluginStatus> GetAll();
    IReadOnlyList<PluginSchema> GetCatalog();
}
