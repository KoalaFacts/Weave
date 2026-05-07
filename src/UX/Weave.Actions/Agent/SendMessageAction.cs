using System.Net;
using System.Net.Http.Json;
using Weave.Actions.Context;

namespace Weave.Actions.Agent;

/// <summary>
/// Phase 2 write verb. POSTs a chat message to
/// <c>POST /api/workspaces/{id}/agents/{name}/messages</c> and translates
/// the silo's response.
/// </summary>
/// <remarks>
/// The silo validates content (non-blank, ≤ 50,000 chars) on its side; 400
/// surfaces as <see cref="ActionFailureReason.ValidationFailed"/> with the
/// silo's per-key error list joined into the message. 409 surfaces as
/// <see cref="ActionFailureReason.Conflict"/> carrying the silo's
/// <c>detail</c> (e.g. agent not running).
/// </remarks>
public sealed class SendMessageAction
{
    private readonly HttpClient _httpClient;

    public SendMessageAction(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ActionResult<SendMessageResult>> ExecuteAsync(
        SendMessageInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.AgentName);
        ArgumentNullException.ThrowIfNull(input.Content);

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                $"/api/workspaces/{Uri.EscapeDataString(input.WorkspaceId)}/agents/{Uri.EscapeDataString(input.AgentName)}/messages",
                new SendMessageWire { Content = input.Content },
                AgentJsonContext.Default.SendMessageWire,
                cancellationToken);

            return response.StatusCode switch
            {
                HttpStatusCode.OK => await ReadSuccessAsync(response, cancellationToken),
                HttpStatusCode.BadRequest => await ReadValidationFailureAsync(response, cancellationToken),
                HttpStatusCode.Conflict => await ReadConflictAsync(response, cancellationToken),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ActionResult.Failed<SendMessageResult>(
                    ActionFailure.Unauthorized($"Silo refused the chat request ({(int)response.StatusCode}).")),
                _ when (int)response.StatusCode >= 500 => ActionResult.Failed<SendMessageResult>(
                    ActionFailure.Internal($"Silo error sending message ({(int)response.StatusCode}).")),
                _ => ActionResult.Failed<SendMessageResult>(
                    ActionFailure.Internal($"Unexpected silo response ({(int)response.StatusCode}).")),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ActionResult.Failed<SendMessageResult>(ActionFailure.Cancelled());
        }
        catch (HttpRequestException ex)
        {
            return ActionResult.Failed<SendMessageResult>(
                ActionFailure.SiloUnreachable($"Silo unreachable: {ex.Message}"));
        }
    }

    private static async Task<ActionResult<SendMessageResult>> ReadSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var wire = await response.Content.ReadFromJsonAsync(
            AgentJsonContext.Default.ChatResponseWire,
            cancellationToken);

        if (wire is null)
        {
            return ActionResult.Failed<SendMessageResult>(
                ActionFailure.Internal("Silo returned an empty chat payload."));
        }

        return ActionResult.Success(new SendMessageResult
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
        });
    }

    private static async Task<ActionResult<SendMessageResult>> ReadValidationFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var problem = await response.Content.ReadFromJsonAsync(
            AgentJsonContext.Default.AgentProblemWire,
            cancellationToken);
        var message = FormatValidationErrors(problem) ?? "Message rejected by the silo.";
        return ActionResult.Failed<SendMessageResult>(
            ActionFailure.ValidationFailed(message));
    }

    private static async Task<ActionResult<SendMessageResult>> ReadConflictAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var problem = await response.Content.ReadFromJsonAsync(
            AgentJsonContext.Default.AgentProblemWire,
            cancellationToken);
        var detail = problem?.Detail;
        return ActionResult.Failed<SendMessageResult>(
            ActionFailure.Conflict(string.IsNullOrWhiteSpace(detail)
                ? "Agent could not handle the message in its current state."
                : detail));
    }

    private static string? FormatValidationErrors(AgentProblemWire? problem)
    {
        if (problem?.Errors is not { } errors || errors.Count == 0)
            return null;

        return string.Join("; ",
            errors.SelectMany(kvp => kvp.Value.Select(message => $"{kvp.Key}: {message}")));
    }
}
