using Weave.Security.Tokens;
using Weave.Tools.Models;

namespace Weave.Tools.Connectors;

public interface IToolConnector
{
    ToolType ToolType { get; }
    Task<ToolHandle> ConnectAsync(ToolSpec tool, CapabilityToken token, CancellationToken ct = default);
    Task DisconnectAsync(ToolHandle handle, CancellationToken ct = default);
    Task<ToolResult> InvokeAsync(ToolHandle handle, ToolInvocation invocation, CancellationToken ct = default);
    Task<ToolSchema> DiscoverSchemaAsync(ToolHandle handle, CancellationToken ct = default);
}
