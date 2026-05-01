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