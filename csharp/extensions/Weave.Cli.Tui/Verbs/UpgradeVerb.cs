namespace Weave.Cli.Tui.Verbs;

internal sealed class UpgradeVerb(UpgradeCliCommand command) : ITuiVerb
{
    public string Name => "upgrade";

    public IReadOnlyList<string> Aliases => ["update"];

    public async Task DispatchAsync(TuiVerbContext context, CancellationToken ct) =>
        await command.ExecuteAsync(new NoCliOptions(), ct);
}
