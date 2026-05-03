using Weave.Security.Tokens;
using Weave.Tools.Models;

namespace Weave.Tools.Actors;

/// <summary>
/// Actor that manages a single tool instance within a workspace.
/// Keyed by {workspaceId}/{toolName}.
/// </summary>
public interface IToolActor
{
    Task<ToolHandle> ConnectAsync(ToolSpec definition, CapabilityToken token);
    Task DisconnectAsync();
    Task<ToolResult> InvokeAsync(ToolInvocation invocation, CapabilityToken token);
    Task<ToolSchema> GetSchemaAsync();
    Task<ToolHandle?> GetHandleAsync();
}
