namespace Weave.Cli.Tui.Verbs;

internal sealed class VersionVerb(VersionCliCommand command) : ITuiVerb
{
    public string Name => "version";

    public IReadOnlyList<string> Aliases => ["v"];

    public async Task DispatchAsync(TuiVerbContext context, CancellationToken ct) =>
        await command.ExecuteAsync(new NoCliOptions(), ct);
}
