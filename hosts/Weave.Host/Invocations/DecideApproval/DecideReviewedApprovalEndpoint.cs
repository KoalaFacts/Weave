using Weave.Invocations;
using Weave.Security.Tokens;
using Weave.Shared.VirtualActors;
using Weave.Silo.VirtualActors;

namespace Weave.Silo.Invocations;

internal static class DecideReviewedApprovalEndpoint
{
    public static async Task<IResult> HandleAsync(HttpContext context, string workspaceId, string toolName,
        string invocationId, ICapabilityTokenService tokens, IVirtualActorProvider actors)
    {
        if (!InvocationHttp.TryAuthenticate(context, workspaceId, toolName, tokens, out var token, out var failure))
            return failure;
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (!InvocationHttp.TryInvocationId(invocationId, out var id))
            return InvocationHttp.Error(400, "invalid-invocation-id");
        var (body, invalid) = await ReadInvocationHttpRequest.ReadJsonAsync(context,
            ApprovalDecisionJsonContext.Default.ApprovalDecisionHttpRequest);
        if (body is null)
            return invalid!;
        if (body.Decision is not ("approve" or "reject") || string.IsNullOrWhiteSpace(body.PlanDigest)
            || body.PlanDigest.Length > 128)
            return InvocationHttp.Error(400, "invalid-approval-decision");
        var (request, invalidRequest) = ReadInvocationHttpRequest.Validate(body.Invocation, toolName);
        if (request is null)
            return invalidRequest!;
        if (request.InvocationId != id)
            return InvocationHttp.Error(400, "invocation-id-mismatch");

        try
        {
            var actor = actors.GetActor<IToolActorGrain>(VirtualActorId.From(workspaceId + "/" + toolName));
            var decision = body.Decision == "approve" ? InvocationApprovalDecision.Approve : InvocationApprovalDecision.Reject;
            var result = await actor.DecideReviewedApprovalWithCancellationAsync(request, body.PlanDigest,
                decision, token, context.RequestAborted);
            if (result.Succeeded && result.Approval is { } approval)
                return Results.Json(new ApprovalHttpStatus(approval.InvocationId.ToString(), approval.State, approval.ExpiresAt),
                    InvocationHttpJsonContext.Default.ApprovalHttpStatus);
            return result.ErrorCode switch
            {
                "approval-not-found" => InvocationHttp.Error(404, result.ErrorCode),
                "invalid-invocation" or "invalid-approval-decision" => InvocationHttp.Error(400, result.ErrorCode),
                "approval-subject-denied" => InvocationHttp.Error(403, "forbidden"),
                _ => InvocationHttp.Error(409, result.ErrorCode!)
            };
        }
        catch (UnauthorizedAccessException)
        {
            return InvocationHttp.Error(403, "forbidden");
        }
    }
}
