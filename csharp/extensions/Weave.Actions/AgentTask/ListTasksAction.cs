using System.Net.Http.Json;
using Weave.Actions.Context;

namespace Weave.Actions.AgentTask;

/// <summary>
/// Phase 1 read-only verb. Self-contained: takes a typed <see cref="HttpClient"/>
/// (BaseAddress configured by the frontend's DI registration), calls the silo's
/// agent-tasks endpoint, and translates the wire shape into the curated
/// <see cref="TaskSummary"/>. No shared silo-client abstraction.
/// </summary>
public sealed class ListTasksAction
{
    private readonly HttpClient _httpClient;

    public ListTasksAction(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ActionResult<ListTasksResult>> ExecuteAsync(
        ListTasksInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.AgentName);

        try
        {
            var endpoint = $"/api/workspaces/{Uri.EscapeDataString(input.WorkspaceId)}"
                + $"/agents/{Uri.EscapeDataString(input.AgentName)}/tasks";

            var wire = await _httpClient.GetFromJsonAsync(
                endpoint,
                TaskJsonContext.Default.ListTaskWire,
                cancellationToken) ?? [];

            var summaries = new TaskSummary[wire.Count];
            for (var i = 0; i < wire.Count; i++)
            {
                var w = wire[i];
                summaries[i] = new TaskSummary
                {
                    TaskId = w.TaskId,
                    Description = w.Description,
                    Status = w.Status,
                    CreatedAt = w.CreatedAt,
                    CompletedAt = w.CompletedAt
                };
            }

            return ActionResult.Success(new ListTasksResult(summaries));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ActionResult.Failed<ListTasksResult>(ActionFailure.Cancelled());
        }
        catch (HttpRequestException ex)
        {
            return ActionResult.Failed<ListTasksResult>(
                ActionFailure.SiloUnreachable($"Silo unreachable: {ex.Message}"));
        }
    }
}
