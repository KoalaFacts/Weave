using System.Net.Http.Json;
using System.Text.Json;
using Weave.Actions.Context;

namespace Weave.Actions.Skill;

/// <summary>
/// Phase 4b verb. Lists skills for a workspace, returning opaque
/// <see cref="JsonElement"/> entries to preserve wire fidelity for the
/// workspace export/import roundtrip — the only callers today.
/// </summary>
public sealed class ListSkillsAction(HttpClient httpClient)
{
    public async Task<ActionResult<ListSkillsResult>> ExecuteAsync(
        ListSkillsInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.WorkspaceId);

        try
        {
            var skills = await httpClient.GetFromJsonAsync(
                $"/api/workspaces/{Uri.EscapeDataString(input.WorkspaceId)}/skills",
                SkillJsonContext.Default.ListJsonElement,
                cancellationToken) ?? [];

            return ActionResult.Success(new ListSkillsResult(skills));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ActionResult.Failed<ListSkillsResult>(ActionFailure.Cancelled());
        }
        catch (HttpRequestException ex)
        {
            return ActionResult.Failed<ListSkillsResult>(
                ActionFailure.SiloUnreachable($"Silo unreachable: {ex.Message}"));
        }
    }
}
