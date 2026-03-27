using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
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
                Error = $"No connector registered for plugin type '{definition.Type}'. " +
                        $"Available: {string.Join(", ", _connectorsByType.Keys)}"
            };
            LogPluginConnectFailed(name, definition.Type, status.Error);
            _active[name] = status;
            return status;
        }

        // Auto-fill config from environment and validate against schema
        var resolved = ResolveConfig(definition, connector.Schema);
        var validationError = ValidateConfig(resolved, connector.Schema);
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
            _active[name] = status;
            return status;
        }

        try
        {
            // Make-before-break: connect the new plugin first. If it fails,
            // the old plugin remains active and uninterrupted.
            var connStatus = await connector.ConnectAsync(name, resolved);

            // Redact secrets from the status info
            var status = connStatus with
            {
                Info = RedactSecrets(connStatus.Info, connector.Schema)
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

    public IReadOnlyList<PluginSchema> GetCatalog() =>
        [.. _connectorsByType.Values.Select(c => c.Schema)];

    /// <summary>
    /// Auto-fill missing config values from environment variables declared in the schema.
    /// Returns a new <see cref="PluginDefinition"/> with resolved config.
    /// </summary>
    internal static PluginDefinition ResolveConfig(PluginDefinition definition, PluginSchema schema)
    {
        var resolved = new Dictionary<string, string>(definition.Config, StringComparer.OrdinalIgnoreCase);

        foreach (var field in schema.Config)
        {
            if (resolved.ContainsKey(field.Name))
                continue;

            // Try environment variable
            if (field.EnvVar is not null)
            {
                var envValue = Environment.GetEnvironmentVariable(field.EnvVar);
                if (envValue is not null)
                {
                    resolved[field.Name] = envValue;
                    continue;
                }
            }

            // Apply default
            if (field.Default is not null)
                resolved[field.Name] = field.Default;
        }

        return definition with { Config = resolved };
    }

    /// <summary>
    /// Validate that all required config fields are present.
    /// Returns an error message, or null if valid.
    /// </summary>
    internal static string? ValidateConfig(PluginDefinition definition, PluginSchema schema)
    {
        var missing = new List<string>();
        foreach (var field in schema.Config)
        {
            if (field.Required && !definition.Config.ContainsKey(field.Name))
                missing.Add(field.EnvVar is not null
                    ? $"'{field.Name}' (or set {field.EnvVar})"
                    : $"'{field.Name}'");
        }

        return missing.Count > 0
            ? $"Missing required config: {string.Join(", ", missing)}"
            : null;
    }

    /// <summary>
    /// Replace secret values in status info with "***".
    /// </summary>
    private static IReadOnlyDictionary<string, string> RedactSecrets(
        IReadOnlyDictionary<string, string> info, PluginSchema schema)
    {
        var secretNames = new HashSet<string>(
            schema.Config.Where(f => f.Secret).Select(f => f.Name),
            StringComparer.OrdinalIgnoreCase);

        if (secretNames.Count == 0)
            return info;

        var redacted = new Dictionary<string, string>(info.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in info)
        {
            redacted[key] = secretNames.Contains(key) ? "***" : value;
        }
        return redacted;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin '{Name}' ({Type}) connected")]
    private partial void LogPluginConnected(string name, string type);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plugin '{Name}' ({Type}) failed to connect: {Error}")]
    private partial void LogPluginConnectFailed(string name, string type, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin '{Name}' ({Type}) disconnected")]
    private partial void LogPluginDisconnected(string name, string type);

    [LoggerMessage(Level = LogLevel.Information, Message = "Hot-swapping plugin '{Name}': {OldType} -> {NewType}")]
    private partial void LogPluginHotSwap(string name, string oldType, string newType);
}
