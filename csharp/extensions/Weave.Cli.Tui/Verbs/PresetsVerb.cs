namespace Weave.Cli.Tui.Verbs;

internal sealed class PresetsVerb(WorkspacePresetsCliCommand command) : ITuiVerb
{
    public string Name => "presets";

    public IReadOnlyList<string> Aliases => ["p"];

    public async Task DispatchAsync(TuiVerbContext context, CancellationToken ct) =>
        await command.ExecuteAsync(new NoCliOptions(), ct);
}
