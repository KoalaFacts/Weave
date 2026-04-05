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
    private readonly Dictionary<ToolType, IToolConnector> _dynamic = [];
    private readonly Lock _lock = new();
    private IReadOnlyList<ToolType>? _cachedTypes;
    private readonly ILogger<ToolDiscoveryService> _logger;

    public ToolDiscoveryService(IEnumerable<IToolConnector> connectors, ILogger<ToolDiscoveryService> logger)
    {
        _logger = logger;
        _builtIn = connectors.ToFrozenDictionary(c => c.ToolType);
        _cachedTypes = [.. _builtIn.Keys];

        LogDiscoveryInitialized(_builtIn.Count, string.Join(", ", _builtIn.Keys));
    }

    public IToolConnector GetConnector(ToolType type)
    {
        lock (_lock)
        {
            if (_dynamic.TryGetValue(type, out var dynamicConnector))
                return dynamicConnector;
        }

        return _builtIn.TryGetValue(type, out var connector)
            ? connector
            : throw new NotSupportedException($"No connector registered for tool type '{type}'");
    }

    public IReadOnlyList<ToolType> SupportedTypes
    {
        get
        {
            lock (_lock)
            {
                return _cachedTypes ??= RebuildTypeCache();
            }
        }
    }

    public void Register(IToolConnector connector)
    {
        lock (_lock)
        {
            _dynamic[connector.ToolType] = connector;
            _cachedTypes = null;
        }
        LogConnectorRegistered(connector.ToolType);
    }

    public bool Unregister(ToolType type)
    {
        bool removed;
        lock (_lock)
        {
            removed = _dynamic.Remove(type);
            if (removed)
                _cachedTypes = null;
        }
        if (removed)
            LogConnectorUnregistered(type);
        return removed;
    }

    private IReadOnlyList<ToolType> RebuildTypeCache()
    {
        var types = new HashSet<ToolType>(_builtIn.Keys);
        foreach (var key in _dynamic.Keys)
            types.Add(key);
        return [.. types];
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Tool discovery initialized with {Count} connector(s): {Types}")]
    private partial void LogDiscoveryInitialized(int count, string types);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dynamic tool connector registered for {Type}")]
    private partial void LogConnectorRegistered(ToolType type);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dynamic tool connector unregistered for {Type}")]
    private partial void LogConnectorUnregistered(ToolType type);
}
