using Weave.Invocations;
using Weave.Security.Tokens;
using Weave.Shared.VirtualActors;
using Weave.Silo.VirtualActors;

namespace Weave.Silo.Invocations;

internal static class GetInvocationEndpoint
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
            var actor = actors.GetActor<IToolActorGrain>(VirtualActorId.From(workspaceId + "/" + toolName));
            var record = await actor.GetInvocationWithCancellationAsync(id, token, context.RequestAborted);
            if (record is null)
                return InvocationHttp.Error(404, "invocation-not-found");
            var result = new InvocationHttpResult
            {
                InvocationId = record.InvocationId.ToString(),
                ToolName = record.ToolName,
                AttemptId = record.Attempt.AttemptId.ToString(),
                Success = record.Attempt.Outcome == InvocationOutcome.Succeeded,
                Outcome = record.Attempt.Outcome,
                OutcomeRecorded = record.Attempt.CompletedAt is not null,
                Duration = record.Attempt.Duration
            };
            return Results.Json(result, InvocationHttpJsonContext.Default.InvocationHttpResult);
        }
        catch (UnauthorizedAccessException)
        {
            return InvocationHttp.Error(403, "forbidden");
        }
    }
}
