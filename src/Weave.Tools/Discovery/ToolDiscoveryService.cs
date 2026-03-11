using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using Weave.Tools.Connectors;
using Weave.Tools.Models;

namespace Weave.Tools.Discovery;

public interface IToolDiscoveryService
{
    IToolConnector GetConnector(ToolType type);
    IReadOnlyList<ToolType> SupportedTypes { get; }
}

public sealed class ToolDiscoveryService : IToolDiscoveryService
{
    private readonly FrozenDictionary<ToolType, IToolConnector> _connectors;
    private readonly IReadOnlyList<ToolType> _supportedTypes;

    public ToolDiscoveryService(IEnumerable<IToolConnector> connectors, ILogger<ToolDiscoveryService> logger)
    {
        _connectors = connectors.ToFrozenDictionary(c => c.ToolType);
        _supportedTypes = [.. _connectors.Keys];

        logger.LogInformation("Tool discovery initialized with {Count} connector(s): {Types}",
            _connectors.Count, string.Join(", ", _connectors.Keys));
    }

    public IToolConnector GetConnector(ToolType type)
    {
        return _connectors.TryGetValue(type, out var connector)
            ? connector
            : throw new NotSupportedException($"No connector registered for tool type '{type}'");
    }

    public IReadOnlyList<ToolType> SupportedTypes => _supportedTypes;
}
