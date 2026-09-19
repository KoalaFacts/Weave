using Weave.Security.Tokens;
using Weave.Shared.VirtualActors;
using Weave.Silo.VirtualActors;

namespace Weave.Silo.Invocations;

internal static class ReviewApprovalEndpoint
{
    public static async Task<IResult> HandleAsync(HttpContext context, string workspaceId, string toolName,
        string invocationId, ICapabilityTokenService tokens, IVirtualActorProvider actors)
    {
        if (!InvocationHttp.TryAuthenticate(context, workspaceId, toolName, tokens, out var token, out var failure))
            return failure;
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (!InvocationHttp.TryInvocationId(invocationId, out var id))
            return InvocationHttp.Error(400, "invalid-invocation-id");
        var (request, invalid) = await ReadInvocationHttpRequest.ReadAsync(context, toolName);
        if (request is null)
            return invalid!;
        if (request.InvocationId != id)
            return InvocationHttp.Error(400, "invocation-id-mismatch");
        try
        {
            var actor = actors.GetActor<IToolActorGrain>(VirtualActorId.From(workspaceId + "/" + toolName));
            var result = await actor.ReviewApprovalWithCancellationAsync(request, token, context.RequestAborted);
            if (result.Review is { } review)
                return Results.Json(ApprovalReviewHttpResponse.FromReview(review),
                    InvocationHttpJsonContext.Default.ApprovalReviewHttpResponse);
            return result.ErrorCode switch
            {
                "approval-not-found" => InvocationHttp.Error(404, result.ErrorCode),
                "invalid-invocation" => InvocationHttp.Error(400, result.ErrorCode),
                _ => InvocationHttp.Error(409, result.ErrorCode!)
            };
        }
        catch (UnauthorizedAccessException)
        {
            return InvocationHttp.Error(403, "forbidden");
        }
    }
}
