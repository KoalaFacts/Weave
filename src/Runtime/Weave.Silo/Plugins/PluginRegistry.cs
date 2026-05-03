using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Weave.Security.Tokens;
using Weave.Workspaces.Models;

namespace Weave.Silo.Plugins;

// --- Registry ---

public sealed partial class PluginRegistry : IPluginRegistry, IDisposable
{
    private readonly Dictionary<string, IPluginConnector> _connectorsByType;
    private readonly ConcurrentDictionary<string, PluginStatus> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly ICapabilityAuthorizer _authorizer;
    // Consumed by the source-generated [LoggerMessage] partial methods below.
    private readonly ILogger<PluginRegistry> _logger;

    public PluginRegistry(
        IEnumerable<IPluginConnector> connectors,
        ICapabilityAuthorizer authorizer,
        ILogger<PluginRegistry> logger)
    {
        _authorizer = authorizer;
        _logger = logger;
        var byType = new Dictionary<string, IPluginConnector>(StringComparer.OrdinalIgnoreCase);
        foreach (var connector in connectors)
        {
            if (!byType.TryAdd(connector.PluginType, connector))
                throw new InvalidOperationException(
                    $"Duplicate plugin connector for type '{connector.PluginType}'. " +
                    $"Each plugin type must have exactly one connector.");
        }
        _connectorsByType = byType;
    }

    public async Task<IReadOnlyList<PluginStatus>> ConnectAllAsync(Dictionary<string, PluginDefinition> plugins, CapabilityToken token)
    {
        var results = new List<PluginStatus>(plugins.Count);
        foreach (var (name, definition) in plugins)
        {
            results.Add(await ConnectAsync(name, definition, token));
        }
        return results;
    }

    public async Task<PluginStatus> ConnectAsync(string name, PluginDefinition definition, CapabilityToken token)
    {
        await _authorizer.AuthorizeAsync(token, $"plugin:invoke:{name}", actorWorkspaceId: null);

        if (!_connectorsByType.TryGetValue(definition.Type, out var connector))
        {
            var status = new PluginStatus
            {
                Name = name,
                Type = definition.Type,
                IsConnected = false,
                Error = $"No connector registered for plugin type '{definition.Type}'. " +
                        $"Available: {string.Join(", ", _connectorsByType.Keys)}"
            };
            LogPluginConnectFailed(name, definition.Type, status.Error);
            return status;
        }

        // Auto-fill config from environment and validate against schema
        var resolved = PluginConfigResolver.Resolve(definition, connector.Schema);
        var validationError = PluginConfigResolver.Validate(resolved, connector.Schema);
        if (validationError is not null)
        {
            var status = new PluginStatus
            {
                Name = name,
                Type = definition.Type,
                IsConnected = false,
                Error = validationError
            };
            LogPluginConnectFailed(name, definition.Type, validationError);
            return status;
        }

        // Per-name lock prevents concurrent connect/disconnect for the same plugin name
        await _connectLock.WaitAsync();
        try
        {
            // Make-before-break: connect the new plugin first. If it fails,
            // the old plugin remains active and uninterrupted.
            var connStatus = await connector.ConnectAsync(name, resolved);

            // Redact secrets from the status info
            var status = connStatus with
            {
                Info = PluginConfigResolver.RedactSecrets(connStatus.Info, connector.Schema)
            };

            if (!status.IsConnected)
            {
                // New plugin failed to connect — leave the old one running
                LogPluginConnectFailed(name, definition.Type, status.Error ?? "unknown");
                return status;
            }

            if (_active.TryGetValue(name, out var existing) && existing.IsConnected)
            {
                LogPluginHotSwap(name, existing.Type, definition.Type);
                if (_connectorsByType.TryGetValue(existing.Type, out var existingConnector))
                    await existingConnector.DisconnectAsync(name);
            }

            _active[name] = status;
            LogPluginConnected(name, definition.Type);
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
            return status;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task<PluginStatus> DisconnectAsync(string name, CapabilityToken token)
    {
        await _authorizer.AuthorizeAsync(token, $"plugin:invoke:{name}", actorWorkspaceId: null);

        await _connectLock.WaitAsync();
        try
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
        finally
        {
            _connectLock.Release();
        }
    }

    public IReadOnlyList<PluginStatus> GetAll()
    {
        // Materialize as a concrete List<T> — a plain collection expression
        // assigned to IReadOnlyList<T> emits a synthesized <>z__ReadOnlyArray
        // that some serializers (notably Orleans) have no codec for.
        List<PluginStatus> statuses = [.. _active.Values];
        return statuses;
    }

    public IReadOnlyList<PluginSchema> GetCatalog()
    {
        List<PluginSchema> schemas = [.. _connectorsByType.Values.Select(c => c.Schema)];
        return schemas;
    }

    public void Dispose() => _connectLock.Dispose();

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin '{Name}' ({Type}) connected")]
    private partial void LogPluginConnected(string name, string type);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plugin '{Name}' ({Type}) failed to connect: {Error}")]
    private partial void LogPluginConnectFailed(string name, string type, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin '{Name}' ({Type}) disconnected")]
    private partial void LogPluginDisconnected(string name, string type);

    [LoggerMessage(Level = LogLevel.Information, Message = "Hot-swapping plugin '{Name}': {OldType} -> {NewType}")]
    private partial void LogPluginHotSwap(string name, string oldType, string newType);
}
