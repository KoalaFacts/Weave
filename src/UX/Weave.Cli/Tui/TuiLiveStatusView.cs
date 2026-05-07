using Spectre.Console;
using Weave.Actions.Context;
using Weave.Actions.Workspace;
using Weave.Cli.Commands;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Tui;

internal sealed class TuiLiveStatusView
{
    private readonly WatchWorkspaceAction _watchAction;
    private readonly TuiLiveStatusWatcher _watcher;

    public TuiLiveStatusView(WatchWorkspaceAction watchAction, TuiLiveStatusWatcher watcher)
    {
        _watchAction = watchAction;
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

        var watch = await _watchAction.ExecuteAsync(new WatchWorkspaceInput(workspaceId), cancellationToken);
        if (!watch.IsSuccess)
        {
            if (watch.Failure.Reason == ActionFailureReason.Cancelled)
                return false;
            CliTheme.WriteWarning($"Live status unavailable: {watch.Failure.Message}. Showing manifest instead.");
            return false;
        }

        AnsiConsole.Write(TuiLiveStatusRenderer.Build(
            manifestPath, manifest, watch.Value.Workspace, watch.Value.Agents, watch.Value.Tools));
        return true;
    }

    public Task WatchAsync(
        string manifestPath,
        WorkspaceManifest manifest,
        CancellationToken cancellationToken) =>
        _watcher.WatchAsync(manifestPath, manifest, cancellationToken);
}
