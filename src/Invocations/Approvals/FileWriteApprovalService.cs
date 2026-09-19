using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Weave.Security.Tokens;
using Weave.Tools.Tool;

namespace Weave.Invocations;

public sealed partial class FileWriteApprovalService(
    IInvocationApprovalStore store,
    IApprovalPlanProtector protector,
    IOptions<InvocationApprovalOptions> options,
    ICapabilityAuthorizer authorizer,
    ICapabilityTokenService tokens,
    TimeProvider timeProvider)
{
    public bool RequiresApproval(ToolSpec spec, ToolInvocation request) =>
        options.Value.RequireFileWriteApproval && spec.Type == ToolType.FileSystem
        && request.Method is not ("read_file" or "list_directory" or "search_files" or "grep" or "file_info");

    internal ToolResult Propose(InvocationRecord candidate, ToolSpec spec, ToolInvocation request,
        CancellationToken cancellationToken)
    {
        var plan = FileWritePlanBinding.Create(candidate, spec, request);
        if (plan is null)
            return Block(candidate.InvocationId, candidate.ToolName, "approval-operation-unsupported");
        var lifetime = options.Value.Lifetime;
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromHours(24))
            return Block(candidate.InvocationId, candidate.ToolName, "approval-policy-invalid");
        var now = timeProvider.GetUtcNow();
        var record = new ApprovalRecord(candidate.InvocationId, candidate.WorkspaceId, candidate.Subject,
            candidate.ToolName, candidate.InputDigest, FileWritePlanBinding.Digest(plan),
            protector.Protect(candidate.WorkspaceId, candidate.InvocationId, FileWritePlanBinding.Serialize(plan)),
            now, now + lifetime, ApprovalState.Pending);
        var stored = store.ProposeApproval(record, cancellationToken);
        if (stored is null || stored.Subject != record.Subject || stored.ToolName != record.ToolName
            || stored.InputDigest != record.InputDigest || stored.PlanDigest != record.PlanDigest)
            return Block(candidate.InvocationId, candidate.ToolName, "invocation-id-conflict");
        return Block(candidate.InvocationId, candidate.ToolName, "approval-required") with
        {
            ApprovalState = stored.StateAt(timeProvider.GetUtcNow())
        };
    }

    internal async Task ValidateResumeAsync(ApprovalRecord approval, InvocationRecord candidate,
        ToolSpec spec, ToolInvocation request, CapabilityToken token)
    {
        await authorizer.AuthorizeAsync(token, ToolCapability.Invoke(spec.Name, "write_file"), approval.WorkspaceId);
        token.CancellationToken.ThrowIfCancellationRequested();
        var plan = FileWritePlanBinding.Create(candidate, spec, request);
        if (!options.Value.RequireFileWriteApproval || plan is null
            || FileWritePlanBinding.Digest(plan) != approval.PlanDigest
            || candidate.InputDigest != approval.InputDigest || token.IssuedTo != approval.Subject
            || approval.ExpiresAt <= timeProvider.GetUtcNow()
            || approval.State is not (ApprovalState.Approved or ApprovalState.Consumed)
            || approval.DecisionTokenId is null || tokens.IsRevoked(approval.DecisionTokenId))
            throw new UnauthorizedAccessException("The exact approved plan or its authority is no longer valid.");
    }

    private FileWriteApprovalPlan Unprotect(ApprovalRecord record)
    {
        var json = protector.Unprotect(record.WorkspaceId, record.InvocationId, record.ProtectedPlan);
        var plan = JsonSerializer.Deserialize(json, ApprovalPlanJsonContext.ForStorage.FileWriteApprovalPlan)
            ?? throw new CryptographicException("Approval plan could not be recovered.");
        if (plan.InvocationId != record.InvocationId || plan.WorkspaceId != record.WorkspaceId
            || plan.Subject != record.Subject || plan.ToolName != record.ToolName
            || FileWritePlanBinding.Digest(plan) != record.PlanDigest)
            throw new CryptographicException("Approval plan does not match its record.");
        return plan;
    }

    internal static ToolResult Block(InvocationId id, string tool, string code) => new()
    {
        InvocationId = id,
        ToolName = tool,
        Success = false,
        ErrorCode = code,
        Error = code == "approval-required" ? "The stored plan requires a separate approval and explicit resume."
            : "The file-write approval requirements were not satisfied; no execution was started."
    };
}
