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

[GenerateSerializer]
public sealed record PluginStatus
{
    [Id(0)] public required string Name { get; init; }
    [Id(1)] public required string Type { get; init; }
    [Id(2)] public bool IsConnected { get; init; }
    [Id(3)] public string? Error { get; init; }
    [Id(4)] public IReadOnlyDictionary<string, string> Info { get; init; } = new Dictionary<string, string>();
}
