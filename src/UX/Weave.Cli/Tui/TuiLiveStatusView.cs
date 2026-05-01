using Spectre.Console;
using Weave.Cli.Commands;
using Weave.Workspaces.Models;

namespace Weave.Cli.Tui;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is kept testable and replaceable from the TUI shell.")]
internal sealed class TuiLiveStatusView
{
    private readonly TuiLiveStatusRenderer _renderer = new();
    private readonly TuiWorkspaceStateReader _stateReader;
    private readonly TuiLiveStatusWatcher _watcher;

    public TuiLiveStatusView()
        : this(new TuiWorkspaceStateReader())
    {
    }

    internal TuiLiveStatusView(TuiWorkspaceStateReader stateReader)
    {
        _stateReader = stateReader;
        _watcher = new TuiLiveStatusWatcher(stateReader);
    }

    public async Task<bool> TryRenderOnceAsync(
        string manifestPath,
        WorkspaceManifest manifest,
        CancellationToken cancellationToken)
    {
        var workspaceId = _stateReader.ReadWorkspaceId(manifestPath);
        if (workspaceId is null)
            return false;

        try
        {
            using var client = new WorkspaceApiClient();
            if (!await client.IsReachableAsync(cancellationToken))
                return false;

            var workspace = await client.GetWorkspaceAsync(workspaceId, cancellationToken);
            var agents = await client.GetAgentsAsync(workspaceId, cancellationToken);
            var tools = await client.GetToolsAsync(workspaceId, cancellationToken);

            AnsiConsole.Write(_renderer.Build(manifestPath, manifest, workspace, agents, tools));
            return true;
        }
        catch (Exception ex)
        {
            CliTheme.WriteWarning($"Live status unavailable: {ex.Message}. Showing manifest instead.");
            return false;
        }
    }

    public async Task WatchAsync(
        string manifestPath,
        WorkspaceManifest manifest,
        CancellationToken cancellationToken) =>
        await _watcher.WatchAsync(manifestPath, manifest, cancellationToken);
}
