using Weave.Actions.Context;

namespace Weave.Actions.Agent;

/// <summary>
/// Phase 1 read-only verb: returns the live agents for a workspace via
/// <see cref="ISiloApi.ListAgentsAsync"/>, surfacing a structured
/// <c>SiloUnreachable</c> failure when the silo can't be reached so the
/// frontend can render a friendly fallback (the TUI shows manifest-only).
/// </summary>
public sealed class ListAgentsAction
{
    private readonly ISiloApi _siloApi;

    public ListAgentsAction(ISiloApi siloApi)
    {
        _siloApi = siloApi;
    }

    public async Task<ActionResult<ListAgentsResult>> ExecuteAsync(
        ListAgentsInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.WorkspaceId);

        try
        {
            var agents = await _siloApi.ListAgentsAsync(input.WorkspaceId, cancellationToken);
            return ActionResult.Success(new ListAgentsResult(agents));
        }
        catch (HttpRequestException ex)
        {
            return ActionResult.Failed<ListAgentsResult>(
                ActionFailure.SiloUnreachable($"Silo unreachable: {ex.Message}"));
        }
    }
}
