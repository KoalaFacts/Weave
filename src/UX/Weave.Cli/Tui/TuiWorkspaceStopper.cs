using Weave.Cli.Commands;

namespace Weave.Cli.Tui;

internal static class TuiWorkspaceStopper
{
    public static async Task StopAsync(TuiSession session, CancellationToken ct)
    {
        if (!session.IsRunning)
        {
            CliTheme.WriteMuted("Workspace is not running.");
            return;
        }

        var command = new WorkspaceDownCliCommand();
        var exitCode = await command.ExecuteAsync(
            new WorkspaceDownOptions(session.WorkspaceName, session.ManifestPath, session.WorkspaceId),
            ct);

        if (exitCode == 0)
            session.MarkStopped();
    }
}
