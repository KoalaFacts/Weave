namespace Weave.Cli.Tui.Verbs;

internal sealed class RefreshVerb(TuiWorkspaceDashboard dashboard) : ITuiVerb
{
    public string Name => "refresh";

    public IReadOnlyList<string> Aliases => ["r"];

    public Task DispatchAsync(TuiVerbContext context, CancellationToken ct) =>
        dashboard.RefreshAsync(ct);
}
