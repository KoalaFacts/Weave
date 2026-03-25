using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Weave.Workspaces.Models;

namespace Weave.Workspaces.Plugins;

/// <summary>
/// Manages plugin lifecycle — connects, disconnects, and hot-swaps plugins.
/// Hot-swap: calling <see cref="ConnectAsync"/> with a name that is already active
/// will disconnect the existing plugin and connect the new one atomically.
/// </summary>
public interface IPluginRegistry
{
    Task<IReadOnlyList<PluginStatus>> ConnectAllAsync(Dictionary<string, PluginDefinition> plugins);
    Task<PluginStatus> ConnectAsync(string name, PluginDefinition definition);
    Task<PluginStatus> DisconnectAsync(string name);
    IReadOnlyList<PluginStatus> GetAll();
}

public sealed partial class PluginRegistry(
    IEnumerable<IPluginConnector> connectors,
    ILogger<PluginRegistry> logger) : IPluginRegistry
{
    private readonly Dictionary<string, IPluginConnector> _connectorsByType =
        connectors.ToDictionary(c => c.PluginType, StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PluginStatus> _active = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<PluginStatus>> ConnectAllAsync(Dictionary<string, PluginDefinition> plugins)
    {
        var results = new List<PluginStatus>(plugins.Count);
        foreach (var (name, definition) in plugins)
        {
            results.Add(await ConnectAsync(name, definition));
        }
        return results;
    }

    public async Task<PluginStatus> ConnectAsync(string name, PluginDefinition definition)
    {
        if (!_connectorsByType.TryGetValue(definition.Type, out var connector))
        {
            var status = new PluginStatus
            {
                Name = name,
                Type = definition.Type,
                IsConnected = false,
                Error = $"No connector registered for plugin type '{definition.Type}'"
            };
            LogPluginConnectFailed(name, definition.Type, status.Error);
            _active[name] = status;
            return status;
        }

        try
        {
            // Hot-swap: disconnect existing plugin with this name before connecting the new one
            if (_active.TryGetValue(name, out var existing) && existing.IsConnected)
            {
                LogPluginHotSwap(name, existing.Type, definition.Type);
                if (_connectorsByType.TryGetValue(existing.Type, out var existingConnector))
                    await existingConnector.DisconnectAsync(name);
            }

            var status = await connector.ConnectAsync(name, definition);
            _active[name] = status;

            if (status.IsConnected)
                LogPluginConnected(name, definition.Type);
            else
                LogPluginConnectFailed(name, definition.Type, status.Error ?? "unknown");

            return status;
        }
        catch (Exception ex)
        {
            var status = new PluginStatus
            {
                Name = name,
                Type = definition.Type,
                IsConnected = false,
                Error = ex.Message
            };
            LogPluginConnectFailed(name, definition.Type, ex.Message);
            _active[name] = status;
            return status;
        }
    }

    public async Task<PluginStatus> DisconnectAsync(string name)
    {
        if (!_active.TryRemove(name, out var existing))
        {
            return new PluginStatus
            {
                Name = name,
                Type = "unknown",
                IsConnected = false,
                Error = $"Plugin '{name}' is not active"
            };
        }

        if (_connectorsByType.TryGetValue(existing.Type, out var connector))
        {
            var status = await connector.DisconnectAsync(name);
            LogPluginDisconnected(name, existing.Type);
            return status;
        }

        return existing with { IsConnected = false };
    }

    public IReadOnlyList<PluginStatus> GetAll() => [.. _active.Values];

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin '{Name}' ({Type}) connected")]
    private partial void LogPluginConnected(string name, string type);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plugin '{Name}' ({Type}) failed to connect: {Error}")]
    private partial void LogPluginConnectFailed(string name, string type, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin '{Name}' ({Type}) disconnected")]
    private partial void LogPluginDisconnected(string name, string type);

    [LoggerMessage(Level = LogLevel.Information, Message = "Hot-swapping plugin '{Name}': {OldType} -> {NewType}")]
    private partial void LogPluginHotSwap(string name, string oldType, string newType);
}
