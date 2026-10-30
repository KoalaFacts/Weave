namespace Weave.Cli.Tui;

internal sealed class TuiCliCommand(IServiceProvider services) : ICliCommand<NoCliOptions>
{
    public string Name => "tui";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Launch the interactive terminal UI";

    public Task<int> ExecuteAsync(NoCliOptions options, CancellationToken ct) => TuiApp.RunAsync(services, ct);
}
