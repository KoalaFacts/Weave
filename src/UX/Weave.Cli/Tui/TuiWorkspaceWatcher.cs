using Weave.Cli.Commands;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.Models;

namespace Weave.Cli.Tui;

internal sealed class TuiWorkspaceWatcher
{
    private readonly ManifestParser _parser = new();
    private readonly TuiLiveStatusView _liveStatus;

    public TuiWorkspaceWatcher(TuiLiveStatusView liveStatus) => _liveStatus = liveStatus;

    public async Task WatchAsync(TuiSession session, CancellationToken ct)
    {
        if (!session.HasWorkspace)
        {
            CliTheme.WriteMuted("No workspace open. Try: /open <workspace>");
            return;
        }
        if (!session.IsRunning)
        {
            CliTheme.WriteMuted($"Workspace '{session.WorkspaceName}' is not running.");
            return;
        }

        WorkspaceManifest manifest;
        try
        {
            var json = await File.ReadAllTextAsync(session.ManifestPath!, ct);
            manifest = _parser.Parse(json);
        }
        catch (Exception ex)
        {
            CliTheme.WriteError($"Failed to parse manifest: {ex.Message}");
            return;
        }

        await _liveStatus.WatchAsync(session.ManifestPath!, manifest, ct);
    }
}
