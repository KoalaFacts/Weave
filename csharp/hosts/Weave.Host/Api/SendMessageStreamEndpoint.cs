using System.Text;
using System.Text.Json;
using Orleans;
using Weave.Agents.Chat;
using Weave.Silo.VirtualActors;

namespace Weave.Silo.Api;

/// <summary>
/// <c>POST /api/workspaces/{id}/agents/{name}/messages/stream</c> — streaming counterpart
/// to <c>SendMessageAsync</c>. Returns <c>text/event-stream</c> with two event types:
/// <c>event: text</c> per token delta and <c>event: complete</c> for the terminal frame
/// (the full <see cref="ChatResponse"/>).
/// </summary>
/// <remarks>
/// <para>Bypasses CQRS to keep the streaming transport contract focused. Lifecycle
/// errors (agent not active) surface as a regular 409 BEFORE any SSE header is written
/// — the first <c>MoveNextAsync</c> is awaited inside a try/catch so the caller still
/// gets a structured failure instead of a half-streamed response.</para>
///
/// <para>Validation failures (empty content, oversize) return JSON 400 the same way
/// the unary endpoint does.</para>
/// </remarks>
internal static class SendMessageStreamEndpoint
{
    private const string TextEventName = "text";
    private const string CompleteEventName = "complete";

    public static async Task HandleAsync(
        string workspaceId,
        string agentName,
        SendMessageRequest request,
        IGrainFactory grains,
        HttpContext context,
        CancellationToken ct)
    {
        var errors = ValidateSendMessage(request);
        if (errors is not null)
        {
            await ResultExtensions.ValidationFailed(errors).ExecuteAsync(context);
            return;
        }

        var grain = grains.GetGrain<IAgentActorGrain>($"{workspaceId}/{agentName}");
        var message = new AgentMessage { Role = request.Role, Content = request.Content };
        var stream = grain.SendStreamingAsync(message, ct);

        await using var enumerator = stream.GetAsyncEnumerator(ct);

        bool hasFirst;
        try
        {
            hasFirst = await enumerator.MoveNextAsync();
        }
        catch (InvalidOperationException ex)
        {
            await ResultExtensions.Conflict(ex.Message).ExecuteAsync(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        if (!hasFirst)
        {
            await context.Response.Body.FlushAsync(ct);
            return;
        }

        do
        {
            await WriteFrameAsync(context.Response, enumerator.Current, ct);
        }
        while (await enumerator.MoveNextAsync());
    }

    private static async Task WriteFrameAsync(HttpResponse response, AgentChatStreamingFrame frame, CancellationToken ct)
    {
        switch (frame)
        {
            case AgentChatTextFrame text:
                await WriteEventAsync(
                    response,
                    TextEventName,
                    JsonSerializer.SerializeToUtf8Bytes(
                        new TextEventWire { Text = text.Text },
                        SiloApiJsonContext.Default.TextEventWire),
                    ct);
                break;

            case AgentChatCompleteFrame complete:
                await WriteEventAsync(
                    response,
                    CompleteEventName,
                    JsonSerializer.SerializeToUtf8Bytes(
                        ChatResponse.FromResponse(complete.Response),
                        SiloApiJsonContext.Default.ChatResponse),
                    ct);
                break;
        }
    }

    private static async Task WriteEventAsync(HttpResponse response, string eventName, byte[] data, CancellationToken ct)
    {
        var prefix = Encoding.UTF8.GetBytes($"event: {eventName}\ndata: ");
        var suffix = Encoding.UTF8.GetBytes("\n\n");
        await response.Body.WriteAsync(prefix, ct);
        await response.Body.WriteAsync(data, ct);
        await response.Body.WriteAsync(suffix, ct);
        await response.Body.FlushAsync(ct);
    }

    private static Dictionary<string, string[]>? ValidateSendMessage(SendMessageRequest request)
    {
        Dictionary<string, string[]>? errors = null;

        if (string.IsNullOrWhiteSpace(request.Content))
            (errors ??= [])["content"] = ["Content is required."];
        else if (request.Content.Length > 50_000)
            (errors ??= [])["content"] = ["Content must be 50000 characters or fewer."];

        return errors;
    }
}
