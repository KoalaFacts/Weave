using System.Text.Json;
using Weave.Tools.Tool;

namespace Weave.Silo.Invocations;

internal static class ReadInvocationHttpRequest
{
    public static async Task<(ToolInvocation? Request, IResult? Failure)> ReadAsync(HttpContext context, string toolName)
    {
        if (!context.Request.HasJsonContentType() || context.Request.Headers.ContentEncoding.Count != 0)
            return (null, InvocationHttp.Error(415, "unsupported-content-type"));
        if (context.Request.ContentLength is > InvocationHttp.MaxBodyBytes)
            return (null, InvocationHttp.Error(413, "request-too-large"));
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
                    return (null, InvocationHttp.Error(413, "request-too-large"));
            }
            var request = JsonSerializer.Deserialize(body.GetBuffer().AsSpan(0, checked((int)body.Length)),
                InvocationHttpJsonContext.Default.InvokeToolHttpRequest);
            if (request is null || request.InvocationId is null
                || !InvocationHttp.TryInvocationId(request.InvocationId, out var id)
                || !string.Equals(request.ToolName, toolName, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(request.Method) || request.Parameters is null
                || request.Parameters.Any(p => p.Value is null))
                return (null, InvocationHttp.Error(400, "invalid-invocation"));
            return (new ToolInvocation
            {
                InvocationId = id,
                ToolName = toolName,
                Method = request.Method,
                Parameters = request.Parameters,
                RawInput = request.RawInput
            }, null);
        }
        catch (JsonException)
        {
            return (null, InvocationHttp.Error(400, "invalid-invocation"));
        }
        catch (BadHttpRequestException error) when (error.StatusCode == 413)
        {
            return (null, InvocationHttp.Error(413, "request-too-large"));
        }
    }
}
