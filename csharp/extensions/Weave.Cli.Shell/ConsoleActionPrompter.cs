using Spectre.Console;
using Weave.Actions.Context;

namespace Weave.Cli.Shell;

/// <summary>
/// CLI binding for <see cref="IActionPrompter"/>: routes through Spectre.Console.
/// Stays oblivious to which action is calling — actions never reference Spectre
/// directly, this adapter is the only seam.
/// </summary>
/// <remarks>
/// Cancellation is best-effort: each method calls
/// <see cref="CancellationToken.ThrowIfCancellationRequested"/> before invoking
/// Spectre. <see cref="AnsiConsole"/>'s prompts block synchronously on stdin
/// and cannot be interrupted mid-prompt — a token signalled while the user is
/// typing is honored only after they hit Enter (and then the action's
/// post-prompt awaits will see the cancellation). Web UI impls can honor
/// mid-prompt cancellation in full.
/// </remarks>
internal sealed class ConsoleActionPrompter : IActionPrompter
{
    public Task<string> PromptTextAsync(
        string message,
        string? defaultValue = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prompt = new TextPrompt<string>(message);
        if (defaultValue is not null)
            prompt.DefaultValue(defaultValue);
        return Task.FromResult(AnsiConsole.Prompt(prompt));
    }

    public Task<string> PromptSelectionAsync(
        string message,
        IReadOnlyList<string> choices,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prompt = new SelectionPrompt<string>()
            .Title(message)
            .AddChoices(choices);
        return Task.FromResult(AnsiConsole.Prompt(prompt));
    }

    public Task<bool> PromptConfirmAsync(
        string message,
        bool defaultValue = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(AnsiConsole.Confirm(message, defaultValue));
    }
}
