using Weave.Invocations;
using Weave.Security.Tokens;
using Weave.Shared.Ids;

namespace Weave.Tools.Tool;

/// <summary>
/// Actor that manages a single tool instance within a workspace.
/// Keyed by {workspaceId}/{toolName}.
/// </summary>
public interface IToolActor
{
    Task<ToolHandle> ConnectAsync(ToolSpec definition, CapabilityToken token);
    Task DisconnectAsync();
    Task<ToolResult> InvokeAsync(ToolInvocation invocation, CapabilityToken token);
    Task<InvocationRecord?> GetInvocationAsync(InvocationId invocationId, CapabilityToken token);
    Task<FileWriteApproval?> GetApprovalAsync(InvocationId id, CapabilityToken token);
    Task<ApprovalDecisionResult> DecideApprovalAsync(InvocationId id, string expectedDigest, ApprovalDecision decision, CapabilityToken token);
    Task<ToolResult> ResumeApprovedAsync(InvocationId id, CapabilityToken token);
    Task<ToolSchema> GetSchemaAsync();
    Task<ToolHandle?> GetHandleAsync();
}
