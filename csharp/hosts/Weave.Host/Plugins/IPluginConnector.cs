using Weave.Workspaces.Manifest;
namespace Weave.Silo.Plugins;

/// <summary>
/// Connects a plugin definition from the workspace manifest to runtime services.
/// Each connector handles one plugin <see cref="PluginDefinition.Type"/>.
/// Reconnecting a name replaces its owned runtime registrations.
/// </summary>
public interface IPluginConnector
{
    string PluginType { get; }
    PluginSchema Schema { get; }
    /// <summary>Every runtime registration this connector may claim for the given name.</summary>
    IReadOnlyList<string> RegistrationKeys(string name);
    Task<PluginStatus> ConnectAsync(string name, PluginDefinition definition);
    Task<PluginStatus> DisconnectAsync(string name);
    PluginStatus GetStatus(string name);
}
