using Weave.Security.Tokens;
using Weave.Tools.Models;

namespace Weave.Tools.Grains;

/// <summary>
/// Grain that manages a single tool instance within a workspace.
/// Keyed by {workspaceId}/{toolName}.
/// </summary>
public interface IToolGrain : IGrainWithStringKey
{
    Task<ToolHandle> ConnectAsync(ToolSpec definition, CapabilityToken token);
    Task DisconnectAsync();
    Task<ToolResult> InvokeAsync(ToolInvocation invocation, CapabilityToken token);
    Task<ToolSchema> GetSchemaAsync();
    Task<ToolHandle?> GetHandleAsync();
}
