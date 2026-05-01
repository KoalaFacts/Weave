using Weave.Cli.Tui;

namespace Weave.Cli.Commands;

internal sealed class TuiCliCommand : ICliCommand<NoCliOptions>
{
    public string Name => "tui";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Launch the interactive terminal UI";

    public Task<int> ExecuteAsync(NoCliOptions options, CancellationToken ct) => TuiApp.RunAsync(ct);
}
