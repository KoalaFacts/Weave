using System.Net.Http.Json;
using Weave.Actions.Context;

namespace Weave.Actions.Skill;

/// <summary>
/// Phase 4b verb. Posts an opaque skill JSON payload to the silo. Used by
/// workspace import to restore previously-exported skills without inspecting
/// their wire shape.
/// </summary>
public sealed class PostSkillAction(HttpClient httpClient)
{
    public async Task<ActionResult<PostSkillResult>> ExecuteAsync(
        PostSkillInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.WorkspaceId);

        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                $"/api/workspaces/{Uri.EscapeDataString(input.WorkspaceId)}/skills",
                input.Skill,
                SkillJsonContext.Default.JsonElement,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return ActionResult.Failed<PostSkillResult>(response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
                        ActionFailure.Unauthorized($"Silo refused skill post: {(int)response.StatusCode}."),
                    System.Net.HttpStatusCode.BadRequest =>
                        ActionFailure.ValidationFailed($"Silo rejected skill payload: {(int)response.StatusCode}."),
                    _ => ActionFailure.Internal($"Silo returned {(int)response.StatusCode} for skill post.")
                });
            }

            return ActionResult.Success(new PostSkillResult());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ActionResult.Failed<PostSkillResult>(ActionFailure.Cancelled());
        }
        catch (HttpRequestException ex)
        {
            return ActionResult.Failed<PostSkillResult>(
                ActionFailure.SiloUnreachable($"Silo unreachable: {ex.Message}"));
        }
    }
}
