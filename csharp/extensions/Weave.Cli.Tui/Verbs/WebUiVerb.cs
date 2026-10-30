namespace Weave.Cli.Tui.Verbs;

internal sealed class WebUiVerb(WebUiCliCommand command) : ITuiVerb
{
    public string Name => "webui";

    public IReadOnlyList<string> Aliases => ["web", "w"];

    public async Task DispatchAsync(TuiVerbContext context, CancellationToken ct) =>
        await command.ExecuteAsync(new WebUiOptions(), ct);
}
