using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Weave.Actions.Context;

namespace Weave.Actions.Agent;

/// <summary>
/// POSTs to <c>POST /api/workspaces/{id}/agents/{name}/messages/stream</c> and
/// yields one chunk per <c>text/event-stream</c> frame.
/// </summary>
/// <remarks>
/// <para>Failure paths surface as a terminal <see cref="SendMessageErrorChunk"/> — 400
/// → <see cref="ActionFailureReason.ValidationFailed"/>, 409 → <c>Conflict</c>,
/// 401/403 → <c>Unauthorized</c>, 5xx and unexpected → <c>Internal</c>,
/// <see cref="HttpRequestException"/> → <c>SiloUnreachable</c>, caller-side
/// cancellation → <c>Cancelled</c>. Failure messages carry the FACT only (no
/// categorical verb prefix) — the frontend pairs <see cref="ActionFailureReason"/>
/// with its own verb at render time. After yielding an error chunk the action
/// stops the underlying SSE read.</para>
///
/// <para>A successful stream emits zero or more <see cref="SendMessageTextChunk"/>
/// followed by exactly one <see cref="SendMessageCompleteChunk"/> carrying the
/// authoritative <see cref="SendMessageResult"/>. If the response body ends without
/// a <c>complete</c> event the action yields an <c>Internal</c> error chunk —
/// the silo's contract is that every successful 200 ends with one.</para>
///
/// <para>Uses <see cref="HttpCompletionOption.ResponseHeadersRead"/> so the response
/// stream is read incrementally as bytes arrive on the wire — without it
/// <see cref="HttpClient"/> would buffer the entire SSE body before returning.</para>
/// </remarks>
public sealed class SendMessageStreamingAction
{
    private readonly HttpClient _httpClient;

    public SendMessageStreamingAction(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async IAsyncEnumerable<SendMessageStreamingChunk> StreamAsync(
        SendMessageInput input,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.AgentName);
        ArgumentNullException.ThrowIfNull(input.Content);

        HttpResponseMessage? response = null;
        SendMessageStreamingChunk? prelude = null;
        try
        {
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"/api/workspaces/{Uri.EscapeDataString(input.WorkspaceId)}/agents/{Uri.EscapeDataString(input.AgentName)}/messages/stream")
                {
                    Content = JsonContent.Create(
                        new SendMessageWire { Content = input.Content },
                        AgentJsonContext.Default.SendMessageWire)
                };

                response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                prelude = new SendMessageErrorChunk(ActionFailure.Cancelled());
            }
            catch (HttpRequestException ex)
            {
                prelude = new SendMessageErrorChunk(ActionFailure.SiloUnreachable(ex.Message));
            }

            if (prelude is not null)
            {
                yield return prelude;
                yield break;
            }

            if (response!.StatusCode != HttpStatusCode.OK)
            {
                yield return await ReadFailureAsync(response, cancellationToken).ConfigureAwait(false);
                yield break;
            }

            await foreach (var chunk in ReadStreamAsync(response, cancellationToken).ConfigureAwait(false))
            {
                yield return chunk;
                if (chunk is SendMessageErrorChunk or SendMessageCompleteChunk)
                    yield break;
            }

            yield return new SendMessageErrorChunk(
                ActionFailure.Internal("stream closed without a complete event"));
        }
        finally
        {
            response?.Dispose();
        }
    }

    private static async IAsyncEnumerable<SendMessageStreamingChunk> ReadStreamAsync(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Stream? stream = null;
        SendMessageErrorChunk? openError = null;
        try
        {
            stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            openError = new SendMessageErrorChunk(
                ActionFailure.SiloUnreachable($"while reading stream: {ex.Message}"));
        }

        if (openError is not null)
        {
            yield return openError;
            yield break;
        }

        await foreach (var sse in SseLineReader.ReadEventsAsync(stream!, cancellationToken).ConfigureAwait(false))
        {
            var chunk = ParseEvent(sse);
            if (chunk is not null)
                yield return chunk;
        }
    }

    private static SendMessageStreamingChunk? ParseEvent(SseEvent sse)
    {
        switch (sse.Event)
        {
            case "text":
                StreamingTextWire? textWire;
                try
                {
                    textWire = JsonSerializer.Deserialize(sse.Data, AgentJsonContext.Default.StreamingTextWire);
                }
                catch (JsonException ex)
                {
                    return new SendMessageErrorChunk(
                        ActionFailure.Internal($"unreadable text frame: {ex.Message}"));
                }
                return textWire is null
                    ? null
                    : new SendMessageTextChunk(textWire.Text);

            case "complete":
                ChatResponseWire? completeWire;
                try
                {
                    completeWire = JsonSerializer.Deserialize(sse.Data, AgentJsonContext.Default.ChatResponseWire);
                }
                catch (JsonException ex)
                {
                    return new SendMessageErrorChunk(
                        ActionFailure.Internal($"unreadable complete frame: {ex.Message}"));
                }
                return completeWire is null
                    ? new SendMessageErrorChunk(ActionFailure.Internal("empty complete frame"))
                    : new SendMessageCompleteChunk(ToResult(completeWire));

            default:
                return null;
        }
    }

    private static SendMessageResult ToResult(ChatResponseWire wire) => new()
    {
        Content = wire.Content,
        ConversationId = wire.ConversationId,
        UsedTools = wire.UsedTools,
        Model = wire.Model,
        Messages = [.. wire.Messages.Select(m => new ConversationMessage
        {
            Role = m.Role,
            Content = m.Content,
            Timestamp = m.Timestamp
        })]
    };

    private static async Task<SendMessageStreamingChunk> ReadFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode is HttpStatusCode.BadRequest)
        {
            var problem = await response.Content.ReadFromJsonAsync(
                AgentJsonContext.Default.AgentProblemWire, cancellationToken).ConfigureAwait(false);
            var message = FormatValidationErrors(problem) ?? "rejected by the silo";
            return new SendMessageErrorChunk(ActionFailure.ValidationFailed(message));
        }

        if (response.StatusCode is HttpStatusCode.Conflict)
        {
            var problem = await response.Content.ReadFromJsonAsync(
                AgentJsonContext.Default.AgentProblemWire, cancellationToken).ConfigureAwait(false);
            var detail = problem?.Detail;
            return new SendMessageErrorChunk(ActionFailure.Conflict(string.IsNullOrWhiteSpace(detail)
                ? "agent could not handle the message in its current state"
                : detail));
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new SendMessageErrorChunk(
                ActionFailure.Unauthorized($"HTTP {(int)response.StatusCode}"));
        }

        if ((int)response.StatusCode >= 500)
        {
            return new SendMessageErrorChunk(
                ActionFailure.Internal($"HTTP {(int)response.StatusCode}"));
        }

        return new SendMessageErrorChunk(
            ActionFailure.Internal($"unexpected response (HTTP {(int)response.StatusCode})"));
    }

    private static string? FormatValidationErrors(AgentProblemWire? problem)
    {
        if (problem?.Errors is not { } errors || errors.Count == 0)
            return null;

        return string.Join("; ",
            errors.SelectMany(kvp => kvp.Value.Select(message => $"{kvp.Key}: {message}")));
    }
}
