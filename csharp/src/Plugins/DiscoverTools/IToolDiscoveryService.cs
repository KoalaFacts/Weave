using Weave.Tools.Connectors;
using Weave.Tools.Tool;
namespace Weave.Tools.Discovery;

public interface IToolDiscoveryService
{
    IToolConnector GetConnector(ToolType type);
    IToolConnector GetConnector(ToolType type, string installationId);
    IReadOnlyList<ToolType> SupportedTypes { get; }
    void Register(IToolConnector connector);
    void Register(string installationId, IToolConnector connector);
    bool Unregister(ToolType type);
    bool UnregisterIfCurrent(ToolType type, IToolConnector connector);
    bool UnregisterIfCurrent(string installationId, ToolType type, IToolConnector connector);
}
