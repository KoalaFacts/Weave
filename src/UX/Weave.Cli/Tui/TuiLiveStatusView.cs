using Spectre.Console;
using Weave.Actions.Agent;
using Weave.Actions.Context;
using Weave.Actions.Tool;
using Weave.Actions.Workspace;
using Weave.Cli.Commands;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Tui;

internal sealed class TuiLiveStatusView
{
    private readonly GetWorkspaceStatusAction _statusAction;
    private readonly ListAgentsAction _agentsAction;
    private readonly ListToolsAction _toolsAction;
    private readonly TuiLiveStatusWatcher _watcher;

    public TuiLiveStatusView(
        GetWorkspaceStatusAction statusAction,
        ListAgentsAction agentsAction,
        ListToolsAction toolsAction,
        TuiLiveStatusWatcher watcher)
    {
        _statusAction = statusAction;
        _agentsAction = agentsAction;
        _toolsAction = toolsAction;
        _watcher = watcher;
    }

    public async Task<bool> TryRenderOnceAsync(
        string manifestPath,
        WorkspaceManifest manifest,
        CancellationToken cancellationToken)
    {
        var workspaceId = TuiWorkspaceStateReader.ReadWorkspaceId(manifestPath);
        if (workspaceId is null)
            return false;

        var statusResult = await _statusAction.ExecuteAsync(new GetWorkspaceStatusInput(workspaceId), cancellationToken);
        if (!statusResult.IsSuccess)
        {
            if (statusResult.Failure.Reason == ActionFailureReason.Cancelled)
                return false;
            CliTheme.WriteWarning($"Live status unavailable: {statusResult.Failure.Message}. Showing manifest instead.");
            return false;
        }

        var agentsResult = await _agentsAction.ExecuteAsync(new ListAgentsInput(workspaceId), cancellationToken);
        var toolsResult = await _toolsAction.ExecuteAsync(new ListToolsInput(workspaceId), cancellationToken);
        var agents = agentsResult.IsSuccess ? agentsResult.Value.Agents : [];
        var tools = toolsResult.IsSuccess ? toolsResult.Value.Tools : [];

        AnsiConsole.Write(TuiLiveStatusRenderer.Build(manifestPath, manifest, statusResult.Value.Workspace, agents, tools));
        return true;
    }

    public Task WatchAsync(
        string manifestPath,
        WorkspaceManifest manifest,
        CancellationToken cancellationToken) =>
        _watcher.WatchAsync(manifestPath, manifest, cancellationToken);
}
