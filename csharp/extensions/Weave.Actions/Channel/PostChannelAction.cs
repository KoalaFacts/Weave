using System.Net.Http.Json;
using Weave.Actions.Context;

namespace Weave.Actions.Channel;

/// <summary>
/// Phase 4b verb. Posts an opaque channel JSON payload to the silo. Used by
/// workspace import to restore previously-exported channels.
/// </summary>
public sealed class PostChannelAction(HttpClient httpClient)
{
    public async Task<ActionResult<PostChannelResult>> ExecuteAsync(
        PostChannelInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.WorkspaceId);

        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                $"/api/workspaces/{Uri.EscapeDataString(input.WorkspaceId)}/channels",
                input.Channel,
                ChannelJsonContext.Default.JsonElement,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return ActionResult.Failed<PostChannelResult>(response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
                        ActionFailure.Unauthorized($"Silo refused channel post: {(int)response.StatusCode}."),
                    System.Net.HttpStatusCode.BadRequest =>
                        ActionFailure.ValidationFailed($"Silo rejected channel payload: {(int)response.StatusCode}."),
                    _ => ActionFailure.Internal($"Silo returned {(int)response.StatusCode} for channel post.")
                });
            }

            return ActionResult.Success(new PostChannelResult());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ActionResult.Failed<PostChannelResult>(ActionFailure.Cancelled());
        }
        catch (HttpRequestException ex)
        {
            return ActionResult.Failed<PostChannelResult>(
                ActionFailure.SiloUnreachable($"Silo unreachable: {ex.Message}"));
        }
    }
}
