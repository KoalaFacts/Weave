using System.Data.Common;
using Weave.Invocations;
using Weave.Security.Tokens;
using Weave.Shared.VirtualActors;
using Weave.Silo.VirtualActors;

namespace Weave.Silo.Invocations;

internal static class DecideStoredApprovalEndpoint
{
    public static async Task<IResult> HandleAsync(HttpContext context, string workspaceId, string toolName,
        string invocationId, ICapabilityTokenService tokens, IInvocationJournal journal,
        ICapabilityAuthorizer authorizer, IVirtualActorProvider actors)
    {
        if (!InvocationHttp.TryAuthenticate(context, workspaceId, toolName, tokens, out var token, out var failure))
            return failure;
        if (!InvocationHttp.TryInvocationId(invocationId, out var id))
            return InvocationHttp.Error(400, "invalid-invocation-id");
        if (context.Request.QueryString.HasValue)
            return InvocationHttp.Error(400, "unexpected-request-input");
        var (body, invalid) = await ReadInvocationHttpRequest.ReadJsonAsync(context,
            StoredApprovalDecisionJsonContext.Default.StoredApprovalDecisionRequest);
        if (body is null)
            return invalid!;
        if (body.Decision is not ("approve" or "reject") || string.IsNullOrWhiteSpace(body.PlanDigest)
            || body.PlanDigest.Length > 128)
            return InvocationHttp.Error(400, "invalid-approval-decision");
        try
        {
            var loaded = await new InvocationProposalReader(journal, authorizer).ReadForReviewAsync(workspaceId, toolName,
                id, token with { CancellationToken = context.RequestAborted });
            if (loaded.Request is not { } request)
                return ProposalHttp.Unavailable(loaded.ErrorCode!);
            var actor = actors.GetActor<IToolActorGrain>(VirtualActorId.From(workspaceId + "/" + toolName));
            var decision = body.Decision == "approve" ? InvocationApprovalDecision.Approve : InvocationApprovalDecision.Reject;
            var result = await actor.DecideReviewedApprovalWithCancellationAsync(request, body.PlanDigest, decision,
                token, context.RequestAborted);
            if (result.Succeeded && result.Approval is { } approval)
                return Results.Json(new ApprovalHttpStatus(approval.InvocationId.ToString(), approval.State, approval.ExpiresAt),
                    InvocationHttpJsonContext.Default.ApprovalHttpStatus);
            return result.ErrorCode == "approval-subject-denied"
                ? InvocationHttp.Error(403, "forbidden") : ProposalHttp.Unavailable(result.ErrorCode!);
        }
        catch (UnauthorizedAccessException)
        {
            return InvocationHttp.Error(403, "forbidden");
        }
        catch (Exception error) when (error is IOException or DbException)
        {
            return InvocationHttp.Error(503, "proposal-storage-unavailable");
        }
    }
}
