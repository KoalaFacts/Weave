using Weave.Tools.Connectors;
using Weave.Tools.Tool;
namespace Weave.Tools.Discovery;

public interface IToolDiscoveryService
{
    IToolConnector GetConnector(ToolType type);
    IReadOnlyList<ToolType> SupportedTypes { get; }
    void Register(IToolConnector connector);
    bool Unregister(ToolType type);
}
