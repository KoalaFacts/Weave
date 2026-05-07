using System.Net;
using System.Net.Http.Json;
using Weave.Actions.Context;

namespace Weave.Actions.Workspace;

/// <summary>
/// Phase 2 write verb. Issues <c>DELETE /api/workspaces/{id}</c>. The silo
/// emits 204 (success), 409 (conflict — already stopped or in a transitional
/// state), or a 5xx; this action maps those onto the standard
/// <see cref="ActionFailure"/> reasons.
/// </summary>
public sealed class StopWorkspaceAction
{
    private readonly HttpClient _httpClient;

    public StopWorkspaceAction(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ActionResult<StopWorkspaceResult>> ExecuteAsync(
        StopWorkspaceInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.WorkspaceId);

        try
        {
            using var response = await _httpClient.DeleteAsync(
                $"/api/workspaces/{Uri.EscapeDataString(input.WorkspaceId)}",
                cancellationToken);

            return response.StatusCode switch
            {
                HttpStatusCode.NoContent => ActionResult.Success(new StopWorkspaceResult()),
                HttpStatusCode.Conflict => await ReadConflictAsync(response, input.WorkspaceId, cancellationToken),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ActionResult.Failed<StopWorkspaceResult>(
                    ActionFailure.Unauthorized($"Silo refused the stop request ({(int)response.StatusCode}).")),
                _ when (int)response.StatusCode >= 500 => ActionResult.Failed<StopWorkspaceResult>(
                    ActionFailure.Internal($"Silo error stopping workspace ({(int)response.StatusCode}).")),
                _ => ActionResult.Failed<StopWorkspaceResult>(
                    ActionFailure.Internal($"Unexpected silo response ({(int)response.StatusCode}).")),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ActionResult.Failed<StopWorkspaceResult>(ActionFailure.Cancelled());
        }
        catch (HttpRequestException ex)
        {
            return ActionResult.Failed<StopWorkspaceResult>(
                ActionFailure.SiloUnreachable($"Silo unreachable: {ex.Message}"));
        }
    }

    private static async Task<ActionResult<StopWorkspaceResult>> ReadConflictAsync(
        HttpResponseMessage response,
        string workspaceId,
        CancellationToken cancellationToken)
    {
        var problem = await response.Content.ReadFromJsonAsync(
            WorkspaceJsonContext.Default.WorkspaceProblemWire,
            cancellationToken);
        var detail = problem?.Detail;
        return ActionResult.Failed<StopWorkspaceResult>(
            ActionFailure.Conflict(string.IsNullOrWhiteSpace(detail)
                ? $"Workspace '{workspaceId}' could not be stopped."
                : detail));
    }
}
