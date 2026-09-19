using System.Text.Json;
using Weave.Invocations;
using Weave.Security.Tokens;
using Weave.Shared.VirtualActors;
using Weave.Tools.Tool;

namespace Weave.Silo.Invocations;

internal static class InvokeToolEndpoint
{
    public static async Task<IResult> HandleAsync(HttpContext context, string workspaceId, string toolName,
        ICapabilityTokenService tokens, IVirtualActorProvider actors)
    {
        if (!InvocationHttp.TryAuthenticate(context, workspaceId, toolName, tokens, out var token, out var failure))
            return failure;
        if (!context.Request.HasJsonContentType() || context.Request.Headers.ContentEncoding.Count != 0)
            return InvocationHttp.Error(415, "unsupported-content-type");
        if (context.Request.ContentLength is > InvocationHttp.MaxBodyBytes)
            return InvocationHttp.Error(413, "request-too-large");

        try
        {
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer.AsMemory(0,
                (int)Math.Min(buffer.Length, InvocationHttp.MaxBodyBytes + 1L - body.Length)), context.RequestAborted)) != 0)
            {
                body.Write(buffer, 0, read);
                if (body.Length > InvocationHttp.MaxBodyBytes)
                    return InvocationHttp.Error(413, "request-too-large");
            }
            var request = JsonSerializer.Deserialize(body.GetBuffer().AsSpan(0, checked((int)body.Length)),
                InvocationHttpJsonContext.Default.ToolInvocation);
            if (request is null || request.InvocationId is null
                || !InvocationHttp.TryInvocationId(request.InvocationId.Value.ToString(), out var id)
                || !string.Equals(request.ToolName, toolName, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(request.Method) || request.Parameters is null
                || request.Parameters.Any(p => p.Value is null))
                return InvocationHttp.Error(400, "invalid-invocation");

            var actor = actors.GetActor<IToolActor>(VirtualActorId.From(workspaceId + "/" + toolName));
            var result = await actor.InvokeAsync(request with { InvocationId = id }, token);
            var status = Status(result);
            if (status == 202)
                context.Response.Headers.Location = $"{context.Request.PathBase}{context.Request.Path}/{id}/approval";
            return Results.Json(result, InvocationHttpJsonContext.Default.ToolResult, statusCode: status);
        }
        catch (JsonException)
        {
            return InvocationHttp.Error(400, "invalid-invocation");
        }
        catch (BadHttpRequestException error) when (error.StatusCode == 413)
        {
            return InvocationHttp.Error(413, "request-too-large");
        }
        catch (UnauthorizedAccessException)
        {
            return InvocationHttp.Error(403, "forbidden");
        }
    }

    private static int Status(ToolResult result)
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
            || result.ErrorCode is "invocation-id-conflict" or "approval-plan-conflict")
            return 409;
        return 422;
    }
}
