using System.Collections.Immutable;
using Weave.Invocations;
using Weave.Security.Tokens;

namespace Weave.Tools.Tool;

public sealed partial class ToolActor
{
    public async Task<InvocationApprovalReviewResult> ReviewApprovalAsync(ToolInvocation invocation, CapabilityToken token)
    {
        EnsureApprovalIdentity();
        _identity.Ensure(invocation: invocation, token: token);
        if (invocation.Parameters is null || string.IsNullOrWhiteSpace(invocation.Method)
            || invocation.InvocationId is not { } suppliedId
            || !Guid.TryParseExact(suppliedId.ToString(), "N", out var guid) || guid == Guid.Empty)
            return new(null, "invalid-invocation");
        var request = invocation with
        {
            InvocationId = InvocationId.From(guid.ToString("N")),
            Parameters = new Dictionary<string, string>(invocation.Parameters, StringComparer.Ordinal)
        };
        token = token with { Grants = new HashSet<string>(token.Grants, StringComparer.Ordinal) };
        var handle = _handle;
        var definition = _definition;
        var version = _connectionVersion;
        var approval = await GetApprovalAsync(request.InvocationId.Value, token);
        if (approval is null)
            return new(null, "approval-not-found");
        await AuthorizeReviewerAsync();
        if (approval.Subject == token.IssuedTo)
            throw new UnauthorizedAccessException("An independent reviewer is required.");
        if (approval.State != InvocationApprovalState.Pending)
            return new(null, "approval-not-pending");
        if (handle is null || definition is null || handle.Type != definition.Type
            || !string.Equals(handle.ToolName, _identity.ToolName, StringComparison.Ordinal))
            return new(null, "approval-review-unavailable");
        var connector = definition.InstallationId is null
            ? discovery.GetConnector(definition.Type)
            : discovery.GetConnector(definition.Type, definition.InstallationId);
        if (connector is not IApprovalTargetBinding binding)
            return new(null, "approval-review-unavailable");
        request = connector.NormalizeInvocation(request);
        _identity.Ensure(invocation: request, token: token);
        request = request with { Parameters = new Dictionary<string, string>(request.Parameters, StringComparer.Ordinal) };
        var inputDigest = InvocationFingerprint.ComputeInputDigest(request,
            approval.WorkspaceId, approval.Subject, definition.Type.ToString());
        if (inputDigest is null || request.Method != approval.Operation || inputDigest != approval.InputDigest)
            return new(null, "approval-plan-conflict");

        var targetDigest = binding.GetApprovalTargetDigest(handle);
        var description = binding.GetApprovalTargetDescription(handle);
        if (string.IsNullOrWhiteSpace(description) || description.Length > 8192
            || InvocationApprovalPlan.BindTarget(targetDigest, inputDigest) != approval.TargetDigest)
            return new(null, "approval-review-unavailable");

        // No secret resolution on behalf of a reviewer. If substitution changed the
        // original effective input, the binding above does not match and no body is returned.
        var current = await GetApprovalAsync(approval.InvocationId, token);
        await AuthorizeReviewerAsync();
        if (current != approval || timeProvider.GetUtcNow() >= approval.ExpiresAt
            || version != _connectionVersion || !ReferenceEquals(handle, _handle)
            || !ReferenceEquals(definition, _definition)
            || targetDigest != binding.GetApprovalTargetDigest(handle)
            || description != binding.GetApprovalTargetDescription(handle))
            return new(null, "approval-plan-conflict");

        return new(new InvocationApprovalReview(approval.InvocationId, approval.WorkspaceId, approval.Subject,
            approval.ToolName, approval.Operation, description, request.Parameters.ToImmutableDictionary(StringComparer.Ordinal), request.RawInput,
            approval.PlanDigest, approval.ExpiresAt), null);

        async Task AuthorizeReviewerAsync()
        {
            await authorizer.AuthorizeAsync(token, "invocation:read", _identity.WorkspaceId);
            await authorizer.AuthorizeAsync(token, "approval:decide", _identity.WorkspaceId);
            await authorizer.AuthorizeAsync(token, ToolCapability.Approve(_identity.ToolName, approval.Operation), _identity.WorkspaceId);
            token.CancellationToken.ThrowIfCancellationRequested();
        }
    }
}
