using Weave.Security.Tokens;
using Weave.Shared.VirtualActors;
using Weave.Tools.Tool;

namespace Weave.Silo.Invocations;

internal static class GetApprovalEndpoint
{
    public static async Task<IResult> HandleAsync(HttpContext context, string workspaceId, string toolName,
        string invocationId, ICapabilityTokenService tokens, IVirtualActorProvider actors)
    {
        if (!InvocationHttp.TryAuthenticate(context, workspaceId, toolName, tokens, out var token, out var failure))
            return failure;
        if (!InvocationHttp.TryInvocationId(invocationId, out var id))
            return InvocationHttp.Error(400, "invalid-invocation-id");
        try
        {
            var actor = actors.GetActor<IToolActor>(VirtualActorId.From(workspaceId + "/" + toolName));
            var approval = await actor.GetApprovalAsync(id, token);
            return approval is null ? InvocationHttp.Error(404, "approval-not-found")
                : Results.Json(new ApprovalHttpStatus(approval.InvocationId.ToString(), approval.State, approval.ExpiresAt),
                    InvocationHttpJsonContext.Default.ApprovalHttpStatus);
        }
        catch (UnauthorizedAccessException)
        {
            return InvocationHttp.Error(403, "forbidden");
        }
    }
}
