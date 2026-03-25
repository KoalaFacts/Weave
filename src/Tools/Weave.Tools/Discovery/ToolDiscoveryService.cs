using System.Collections.Concurrent;
using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using Weave.Tools.Connectors;
using Weave.Tools.Models;

namespace Weave.Tools.Discovery;

public interface IToolDiscoveryService
{
    IToolConnector GetConnector(ToolType type);
    IReadOnlyList<ToolType> SupportedTypes { get; }
    void Register(IToolConnector connector);
    bool Unregister(ToolType type);
}

public sealed partial class ToolDiscoveryService : IToolDiscoveryService
{
    private readonly FrozenDictionary<ToolType, IToolConnector> _builtIn;
    private readonly ConcurrentDictionary<ToolType, IToolConnector> _dynamic = new();
    private readonly ILogger<ToolDiscoveryService> _logger;
    private volatile IReadOnlyList<ToolType>? _cachedTypes;

    public ToolDiscoveryService(IEnumerable<IToolConnector> connectors, ILogger<ToolDiscoveryService> logger)
    {
        _logger = logger;
        _builtIn = connectors.ToFrozenDictionary(c => c.ToolType);
        _cachedTypes = [.. _builtIn.Keys];

        LogDiscoveryInitialized(_builtIn.Count, string.Join(", ", _builtIn.Keys));
    }

    public IToolConnector GetConnector(ToolType type)
    {
        if (_dynamic.TryGetValue(type, out var dynamicConnector))
            return dynamicConnector;

        return _builtIn.TryGetValue(type, out var connector)
            ? connector
            : throw new NotSupportedException($"No connector registered for tool type '{type}'");
    }

    public IReadOnlyList<ToolType> SupportedTypes =>
        _cachedTypes ?? RebuildTypeCache();

    /// <summary>
    /// Register a tool connector at runtime (e.g., from a plugin).
    /// Overrides any built-in connector for the same <see cref="ToolType"/>.
    /// </summary>
    public void Register(IToolConnector connector)
    {
        _dynamic[connector.ToolType] = connector;
        _cachedTypes = null;
        LogConnectorRegistered(connector.ToolType);
    }

    /// <summary>
    /// Remove a dynamically registered connector. Built-in connectors are not affected.
    /// </summary>
    public bool Unregister(ToolType type)
    {
        var removed = _dynamic.TryRemove(type, out _);
        if (removed)
        {
            _cachedTypes = null;
            LogConnectorUnregistered(type);
        }
        return removed;
    }

    private IReadOnlyList<ToolType> RebuildTypeCache()
    {
        var types = new HashSet<ToolType>(_builtIn.Keys);
        foreach (var key in _dynamic.Keys)
            types.Add(key);
        var result = (IReadOnlyList<ToolType>)[.. types];
        _cachedTypes = result;
        return result;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Tool discovery initialized with {Count} connector(s): {Types}")]
    private partial void LogDiscoveryInitialized(int count, string types);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dynamic tool connector registered for {Type}")]
    private partial void LogConnectorRegistered(ToolType type);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dynamic tool connector unregistered for {Type}")]
    private partial void LogConnectorUnregistered(ToolType type);
}
