namespace Weave.Cli.Tui.Verbs;

internal sealed class DownVerb(WorkspaceDownCliCommand command) : ITuiVerb
{
    public string Name => "down";

    public IReadOnlyList<string> Aliases => [];

    public async Task DispatchAsync(TuiVerbContext context, CancellationToken ct)
    {
        var session = context.Session;
        if (!session.IsRunning)
        {
            CliTheme.WriteMuted("Workspace is not running.");
            return;
        }

        var exitCode = await command.ExecuteAsync(
            new WorkspaceDownOptions(session.WorkspaceName, session.ManifestPath, session.WorkspaceId),
            ct);

        if (exitCode == 0)
            session.MarkStopped();
    }
}
