namespace Weave.Cli.Tui.Verbs;

internal sealed class StatusVerb(WorkspaceStatusCliCommand command) : ITuiVerb
{
    public string Name => "status";

    public IReadOnlyList<string> Aliases => [];

    public async Task DispatchAsync(TuiVerbContext context, CancellationToken ct)
    {
        if (!context.Session.HasWorkspace)
        {
            CliTheme.WriteMuted("No workspace open. Try: /open <workspace>");
            return;
        }

        await command.ExecuteAsync(new WorkspaceNameOptions(context.Session.WorkspaceName), ct);
    }
}
