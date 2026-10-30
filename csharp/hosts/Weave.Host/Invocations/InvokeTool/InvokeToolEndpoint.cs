using Weave.Invocations;
using Weave.Security.Tokens;
using Weave.Shared.VirtualActors;
using Weave.Silo.VirtualActors;
using Weave.Tools.Tool;

namespace Weave.Silo.Invocations;

internal static class InvokeToolEndpoint
{
    public static async Task<IResult> HandleAsync(HttpContext context, string workspaceId, string toolName,
        ICapabilityTokenService tokens, IVirtualActorProvider actors)
    {
        if (!InvocationHttp.TryAuthenticate(context, workspaceId, toolName, tokens, out var token, out var failure))
            return failure;
        var (request, invalid) = await ReadInvocationHttpRequest.ReadAsync(context, toolName);
        if (request is null)
            return invalid!;
        try
        {
            var actor = actors.GetActor<IToolActorGrain>(VirtualActorId.From(workspaceId + "/" + toolName));
            var result = await actor.InvokeWithCancellationAsync(request, token, context.RequestAborted);
            var status = Status(result);
            if (status == 202)
                context.Response.Headers.Location = $"{context.Request.PathBase}/api/workspaces/{workspaceId}/tools/{toolName}/invocations/{request.InvocationId}/approval";
            return Results.Json(InvocationHttpResult.FromResult(result),
                InvocationHttpJsonContext.Default.InvocationHttpResult, statusCode: status);
        }
        catch (UnauthorizedAccessException)
        {
            return InvocationHttp.Error(403, "forbidden");
        }
    }

    internal static int Status(ToolResult result)
    {
        if (result.Success)
            return 200;
        if (result.ErrorCode == "approval-pending")
            return 202;
        if (result.ErrorCode == "journal-write-failed")
            return 503;
        if (result.ErrorCode == "invalid-invocation")
            return 400;
        if (result.Outcome == InvocationOutcome.OutcomeUnknown || result.IsReplay || result.ApprovalState is not null
            || result.ErrorCode is "invocation-id-conflict" or "approval-plan-conflict" or "proposal-unavailable")
            return 409;
        return 422;
    }
}
