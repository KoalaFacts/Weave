using Weave.Cli.Commands.Local;

namespace Weave.Cli.Tests;

internal sealed class LocalCliNoninteractiveReview : ILocalReviewConsole
{
    public bool IsInteractive => false;
    public List<string> Lines { get; } = [];
    public void WriteLine(string text) => Lines.Add(text);
    public ValueTask<string?> ReadLineAsync(CancellationToken ct) =>
        throw new InvalidOperationException("Noninteractive review must not prompt for a decision.");
}
