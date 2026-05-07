using Weave.Actions.Agent;
using Weave.Actions.Context;
using Weave.Actions.Tool;

namespace Weave.Actions.Workspace;

/// <summary>
/// Phase 2 write verb. Single-shot composite of
/// <see cref="GetWorkspaceStatusAction"/>, <see cref="ListAgentsAction"/>,
/// and <see cref="ListToolsAction"/>. Frontends drive the polling loop
/// themselves; this action just stitches one tick.
/// </summary>
/// <remarks>
/// Status is the required component — its failure short-circuits the
/// snapshot. Agents and tools are best-effort: a transient failure on a
/// polling tick yields an empty list rather than failing the whole frame,
/// matching the existing TUI watcher's "Loading…" / "Silo unreachable.
/// Retrying…" UX shape.
/// </remarks>
public sealed class WatchWorkspaceAction
{
    private readonly GetWorkspaceStatusAction _statusAction;
    private readonly ListAgentsAction _agentsAction;
    private readonly ListToolsAction _toolsAction;

    public WatchWorkspaceAction(
        GetWorkspaceStatusAction statusAction,
        ListAgentsAction agentsAction,
        ListToolsAction toolsAction)
    {
        _statusAction = statusAction;
        _agentsAction = agentsAction;
        _toolsAction = toolsAction;
    }

    public async Task<ActionResult<WatchWorkspaceResult>> ExecuteAsync(
        WatchWorkspaceInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.WorkspaceId);

        var status = await _statusAction.ExecuteAsync(
            new GetWorkspaceStatusInput(input.WorkspaceId),
            cancellationToken);
        if (!status.IsSuccess)
            return ActionResult.Failed<WatchWorkspaceResult>(status.Failure);

        var agents = await _agentsAction.ExecuteAsync(
            new ListAgentsInput(input.WorkspaceId),
            cancellationToken);
        var tools = await _toolsAction.ExecuteAsync(
            new ListToolsInput(input.WorkspaceId),
            cancellationToken);

        return ActionResult.Success(new WatchWorkspaceResult(
            status.Value.Workspace,
            agents.IsSuccess ? agents.Value.Agents : [],
            tools.IsSuccess ? tools.Value.Tools : []));
    }
}
