using Weave.Cli.Commands;

namespace Weave.Cli.Tui;

internal sealed class TuiWorkspaceStopper
{
    private readonly WorkspaceDownCliCommand _downCommand;

    public TuiWorkspaceStopper(WorkspaceDownCliCommand downCommand)
    {
        _downCommand = downCommand;
    }

    public async Task StopAsync(TuiSession session, CancellationToken ct)
    {
        if (!session.IsRunning)
        {
            CliTheme.WriteMuted("Workspace is not running.");
            return;
        }

        var exitCode = await _downCommand.ExecuteAsync(
            new WorkspaceDownOptions(session.WorkspaceName, session.ManifestPath, session.WorkspaceId),
            ct);

        if (exitCode == 0)
            session.MarkStopped();
    }
}
