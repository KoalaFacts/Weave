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
    private readonly Dictionary<ToolType, IToolConnector> _connectors;
    private readonly ILogger<ToolDiscoveryService> _logger;

    public ToolDiscoveryService(IEnumerable<IToolConnector> connectors, ILogger<ToolDiscoveryService> logger)
    {
        _logger = logger;
        _connectors = connectors.ToDictionary(c => c.ToolType);

        _logger.LogInformation("Tool discovery initialized with {Count} connector(s): {Types}",
            _connectors.Count, string.Join(", ", _connectors.Keys));
    }

    public IToolConnector GetConnector(ToolType type)
    {
        return _connectors.TryGetValue(type, out var connector)
            ? connector
            : throw new NotSupportedException($"No connector registered for tool type '{type}'");
    }

    public IReadOnlyList<ToolType> SupportedTypes => _connectors.Keys.ToList();
}
