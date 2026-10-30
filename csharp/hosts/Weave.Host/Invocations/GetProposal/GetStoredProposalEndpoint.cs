using System.Data.Common;
using Weave.Invocations;
using Weave.Security.Tokens;

namespace Weave.Silo.Invocations;

internal static class GetStoredProposalEndpoint
{
    public static async Task<IResult> HandleAsync(HttpContext context, string workspaceId, string toolName,
        string invocationId, ICapabilityTokenService tokens, IInvocationJournal journal, ICapabilityAuthorizer authorizer)
    {
        if (!InvocationHttp.TryAuthenticate(context, workspaceId, toolName, tokens, out var token, out var failure))
            return failure;
        if (!InvocationHttp.TryInvocationId(invocationId, out var id))
            return InvocationHttp.Error(400, "invalid-invocation-id");
        if (ProposalHttp.HasUnexpectedInput(context))
            return InvocationHttp.Error(400, "unexpected-request-input");
        try
        {
            var result = await new InvocationProposalReader(journal, authorizer).ReadOwnedAsync(workspaceId, toolName,
                id, token with { CancellationToken = context.RequestAborted });
            if (result.Request is not { } request)
                return ProposalHttp.Unavailable(result.ErrorCode!);
            return Results.Json(new InvokeToolHttpRequest
            {
                InvocationId = request.InvocationId!.Value.ToString(),
                ToolName = request.ToolName,
                Method = request.Method,
                Parameters = request.Parameters,
                RawInput = request.RawInput
            }, InvocationHttpJsonContext.Default.InvokeToolHttpRequest);
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
