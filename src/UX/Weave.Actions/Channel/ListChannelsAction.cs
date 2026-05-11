using System.Net.Http.Json;
using Weave.Actions.Context;

namespace Weave.Actions.Channel;

/// <summary>
/// Phase 4b verb. Lists channels for a workspace as opaque
/// <see cref="System.Text.Json.JsonElement"/> entries to preserve wire fidelity
/// for the workspace export/import roundtrip.
/// </summary>
public sealed class ListChannelsAction(HttpClient httpClient)
{
    public async Task<ActionResult<ListChannelsResult>> ExecuteAsync(
        ListChannelsInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.WorkspaceId);

        try
        {
            var channels = await httpClient.GetFromJsonAsync(
                $"/api/workspaces/{Uri.EscapeDataString(input.WorkspaceId)}/channels",
                ChannelJsonContext.Default.ListJsonElement,
                cancellationToken) ?? [];

            return ActionResult.Success(new ListChannelsResult(channels));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ActionResult.Failed<ListChannelsResult>(ActionFailure.Cancelled());
        }
        catch (HttpRequestException ex)
        {
            return ActionResult.Failed<ListChannelsResult>(
                ActionFailure.SiloUnreachable($"Silo unreachable: {ex.Message}"));
        }
    }
}
