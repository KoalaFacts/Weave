using System.Net;
using System.Net.Http.Json;
using Weave.Actions.Context;

namespace Weave.Actions.Workspace;

/// <summary>
/// Phase 1 read-only verb. Self-contained: takes a typed <see cref="HttpClient"/>
/// (BaseAddress configured by the frontend's DI registration), calls the silo's
/// per-workspace endpoint, and translates the wire shape into the curated
/// <see cref="WorkspaceStatusSummary"/>. No shared silo-client abstraction.
/// </summary>
public sealed class GetWorkspaceStatusAction
{
    private readonly HttpClient _httpClient;

    public GetWorkspaceStatusAction(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ActionResult<GetWorkspaceStatusResult>> ExecuteAsync(
        GetWorkspaceStatusInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.WorkspaceId);

        try
        {
            using var response = await _httpClient.GetAsync(
                $"/api/workspaces/{Uri.EscapeDataString(input.WorkspaceId)}",
                cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return ActionResult.Failed<GetWorkspaceStatusResult>(
                    ActionFailure.NotFound($"Workspace '{input.WorkspaceId}' is not running."));
            }

            response.EnsureSuccessStatusCode();

            var wire = await response.Content.ReadFromJsonAsync(
                WorkspaceJsonContext.Default.WorkspaceWire,
                cancellationToken);

            if (wire is null)
            {
                return ActionResult.Failed<GetWorkspaceStatusResult>(
                    ActionFailure.Internal("Silo returned an empty workspace payload."));
            }

            return ActionResult.Success(new GetWorkspaceStatusResult(new WorkspaceStatusSummary
            {
                WorkspaceId = wire.WorkspaceId,
                Name = wire.Name,
                Status = wire.Status,
                ContainerCount = wire.ContainerCount,
                StartedAt = wire.StartedAt,
                StoppedAt = wire.StoppedAt,
                NetworkId = wire.NetworkId,
                ErrorMessage = wire.ErrorMessage
            }));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ActionResult.Failed<GetWorkspaceStatusResult>(ActionFailure.Cancelled());
        }
        catch (HttpRequestException ex)
        {
            return ActionResult.Failed<GetWorkspaceStatusResult>(
                ActionFailure.SiloUnreachable($"Silo unreachable: {ex.Message}"));
        }
    }
}
