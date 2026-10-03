namespace Weave.Cli.Commands.Local;

internal interface ILocalReviewConsole
{
    bool IsInteractive { get; }
    void WriteLine(string text);
    ValueTask<string?> ReadLineAsync(CancellationToken ct);
}
