using System.Net.Http.Json;
using Weave.Actions.Context;

namespace Weave.Actions.Tool;

/// <summary>
/// Phase 1 read-only verb. Self-contained: takes a typed <see cref="HttpClient"/>
/// (BaseAddress configured by the frontend's DI registration), calls the silo's
/// tools endpoint, and translates the wire shape into the curated
/// <see cref="ToolSummary"/>. No shared silo-client abstraction.
/// </summary>
public sealed class ListToolsAction
{
    private readonly HttpClient _httpClient;

    public ListToolsAction(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ActionResult<ListToolsResult>> ExecuteAsync(
        ListToolsInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.WorkspaceId);

        try
        {
            var wire = await _httpClient.GetFromJsonAsync(
                $"/api/workspaces/{Uri.EscapeDataString(input.WorkspaceId)}/tools",
                ToolJsonContext.Default.ListToolWire,
                cancellationToken) ?? [];

            var summaries = new ToolSummary[wire.Count];
            for (var i = 0; i < wire.Count; i++)
            {
                var w = wire[i];
                summaries[i] = new ToolSummary
                {
                    ToolName = w.ToolName,
                    ToolType = w.ToolType,
                    Status = w.Status,
                    Endpoint = w.Endpoint,
                    ConnectedAt = w.ConnectedAt,
                    ErrorMessage = w.ErrorMessage
                };
            }

            return ActionResult.Success(new ListToolsResult(summaries));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ActionResult.Failed<ListToolsResult>(ActionFailure.Cancelled());
        }
        catch (HttpRequestException ex)
        {
            return ActionResult.Failed<ListToolsResult>(
                ActionFailure.SiloUnreachable($"Silo unreachable: {ex.Message}"));
        }
    }
}
