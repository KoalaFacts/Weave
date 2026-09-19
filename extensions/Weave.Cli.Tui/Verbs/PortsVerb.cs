namespace Weave.Cli.Tui.Verbs;

internal sealed class PortsVerb(PortsCliCommand command) : ITuiVerb
{
    public string Name => "ports";

    public IReadOnlyList<string> Aliases => [];

    public async Task DispatchAsync(TuiVerbContext context, CancellationToken ct) =>
        await command.ExecuteAsync(new NoCliOptions(), ct);
}
