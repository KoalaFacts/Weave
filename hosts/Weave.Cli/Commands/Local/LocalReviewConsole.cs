namespace Weave.Cli.Commands.Local;

internal sealed class LocalReviewConsole : ILocalReviewConsole
{
    public bool IsInteractive => !Console.IsInputRedirected && !Console.IsOutputRedirected;
    public void WriteLine(string text) => Console.WriteLine(text);
    public ValueTask<string?> ReadLineAsync(CancellationToken ct) => Console.In.ReadLineAsync(ct);
}
