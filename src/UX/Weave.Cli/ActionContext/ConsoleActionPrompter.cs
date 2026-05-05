using Spectre.Console;
using Weave.Actions.Context;

namespace Weave.Cli.ActionContext;

/// <summary>
/// CLI binding for <see cref="IActionPrompter"/>: routes through Spectre.Console.
/// Stays oblivious to which action is calling — actions never reference Spectre
/// directly, this adapter is the only seam.
/// </summary>
internal sealed class ConsoleActionPrompter : IActionPrompter
{
    public Task<string> PromptTextAsync(
        string message,
        string? defaultValue = null,
        CancellationToken cancellationToken = default)
    {
        var prompt = new TextPrompt<string>(message);
        if (defaultValue is not null)
            prompt.DefaultValue(defaultValue);
        return Task.FromResult(AnsiConsole.Prompt(prompt));
    }

    public Task<string> PromptSelectionAsync(
        string message,
        IReadOnlyList<string> choices,
        string? defaultValue = null,
        CancellationToken cancellationToken = default)
    {
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
        return Task.FromResult(AnsiConsole.Confirm(message, defaultValue));
    }
}
