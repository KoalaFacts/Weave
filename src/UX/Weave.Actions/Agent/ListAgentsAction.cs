using System.Net.Http.Json;
using Weave.Actions.Context;

namespace Weave.Actions.Agent;

/// <summary>
/// Phase 1 read-only verb. Self-contained: takes a typed <see cref="HttpClient"/>
/// (BaseAddress configured by the frontend's DI registration), calls the silo's
/// agents endpoint, and translates the wire shape into the curated
/// <see cref="AgentSummary"/>. No shared silo-client abstraction; if a future
/// action needs the same wire shape, it duplicates 5 lines rather than coupling
/// to a growing seam.
/// </summary>
public sealed class ListAgentsAction
{
    private readonly HttpClient _httpClient;

    public ListAgentsAction(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ActionResult<ListAgentsResult>> ExecuteAsync(
        ListAgentsInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.WorkspaceId);

        try
        {
            var wire = await _httpClient.GetFromJsonAsync(
                $"/api/workspaces/{Uri.EscapeDataString(input.WorkspaceId)}/agents",
                AgentJsonContext.Default.ListAgentWire,
                cancellationToken) ?? [];

            var summaries = new AgentSummary[wire.Count];
            for (var i = 0; i < wire.Count; i++)
            {
                var w = wire[i];
                summaries[i] = new AgentSummary
                {
                    AgentName = w.AgentName,
                    Status = w.Status,
                    Model = w.Model,
                    ActiveTasksCount = w.ActiveTasks?.Count ?? 0,
                    ConnectedToolsCount = w.ConnectedTools?.Count ?? 0
                };
            }

            return ActionResult.Success(new ListAgentsResult(summaries));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ActionResult.Failed<ListAgentsResult>(ActionFailure.Cancelled());
        }
        catch (HttpRequestException ex)
        {
            return ActionResult.Failed<ListAgentsResult>(
                ActionFailure.SiloUnreachable($"Silo unreachable: {ex.Message}"));
        }
    }
}
