using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Weave.Workspaces.Models;

namespace Weave.Workspaces.Plugins;

/// <summary>
/// Manages plugin lifecycle — connects and disconnects plugins based on manifest config.
/// Used by the Silo at workspace startup to activate declared plugins.
/// </summary>
public interface IPluginRegistry
{
    IReadOnlyList<PluginStatus> ConnectAll(Dictionary<string, PluginDefinition> plugins);
    PluginStatus Connect(string name, PluginDefinition definition);
    PluginStatus Disconnect(string name);
    IReadOnlyList<PluginStatus> GetAll();
}

public sealed partial class PluginRegistry(
    IEnumerable<IPluginConnector> connectors,
    ILogger<PluginRegistry> logger) : IPluginRegistry
{
    private readonly Dictionary<string, IPluginConnector> _connectorsByType =
        connectors.ToDictionary(c => c.PluginType, StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PluginStatus> _active = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<PluginStatus> ConnectAll(Dictionary<string, PluginDefinition> plugins)
    {
        var results = new List<PluginStatus>(plugins.Count);
        foreach (var (name, definition) in plugins)
        {
            results.Add(Connect(name, definition));
        }
        return results;
    }

    public PluginStatus Connect(string name, PluginDefinition definition)
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
            var status = connector.Connect(name, definition);
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

    public PluginStatus Disconnect(string name)
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
            var status = connector.Disconnect(name);
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
}
