using System.Text;

namespace Weave.Cli.Commands.Local;

internal sealed class LocalReviewConsole : ILocalReviewConsole
{
    private bool _utf8Configured;
    public bool IsInteractive => !Console.IsInputRedirected && !Console.IsOutputRedirected;
    public void WriteLine(string text)
    {
        if (!_utf8Configured)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            _utf8Configured = true;
        }
        Console.WriteLine(text);
    }
    public ValueTask<string?> ReadLineAsync(CancellationToken ct) => Console.In.ReadLineAsync(ct);
}
