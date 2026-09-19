using Weave.Security.Tokens;
using Weave.Tools.Tool;
namespace Weave.Tools.Connectors;

public interface IToolConnector
{
    ToolType ToolType { get; }

    /// <summary>Pure normalization of the execution selector before authorization. Must not perform IO or retarget the tool.</summary>
    ToolInvocation NormalizeInvocation(ToolInvocation invocation);
    Task<ToolHandle> ConnectAsync(ToolSpec tool, CapabilityToken token, CancellationToken ct = default);
    Task DisconnectAsync(ToolHandle handle, CancellationToken ct = default);
    Task<ToolResult> InvokeAsync(ToolHandle handle, ToolInvocation invocation, CancellationToken ct = default);
    Task<ToolSchema> DiscoverSchemaAsync(ToolHandle handle, CancellationToken ct = default);
}
