using System.Data.Common;
using Weave.Invocations;
using Weave.Security.Tokens;
using Weave.Shared.VirtualActors;
using Weave.Silo.VirtualActors;

namespace Weave.Silo.Invocations;

internal static class ReviewStoredApprovalEndpoint
{
    public static async Task<IResult> HandleAsync(HttpContext context, string workspaceId, string toolName,
        string invocationId, ICapabilityTokenService tokens, IInvocationJournal journal,
        ICapabilityAuthorizer authorizer, IVirtualActorProvider actors)
    {
        if (!InvocationHttp.TryAuthenticate(context, workspaceId, toolName, tokens, out var token, out var failure))
            return failure;
        if (!InvocationHttp.TryInvocationId(invocationId, out var id))
            return InvocationHttp.Error(400, "invalid-invocation-id");
        if (ProposalHttp.HasUnexpectedInput(context))
            return InvocationHttp.Error(400, "unexpected-request-input");
        try
        {
            var loaded = await new InvocationProposalReader(journal, authorizer).ReadForReviewAsync(workspaceId, toolName,
                id, token with { CancellationToken = context.RequestAborted });
            if (loaded.Request is not { } request)
                return ProposalHttp.Unavailable(loaded.ErrorCode!);
            var actor = actors.GetActor<IToolActorGrain>(VirtualActorId.From(workspaceId + "/" + toolName));
            var result = await actor.ReviewApprovalWithCancellationAsync(request, token, context.RequestAborted);
            return result.Review is { } review
                ? Results.Json(ApprovalReviewHttpResponse.FromReview(review), InvocationHttpJsonContext.Default.ApprovalReviewHttpResponse)
                : ProposalHttp.Unavailable(result.ErrorCode!);
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
