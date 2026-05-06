namespace Weave.Actions.Context;

/// <summary>
/// Asks the user for missing input. Frontend-supplied: the CLI uses Spectre
/// prompts; a Web UI would honor cancellation when a modal closes or a
/// websocket drops; test fakes return canned answers.
/// </summary>
/// <remarks>
/// Actions never instantiate prompts inline — they call this seam so the same
/// orchestration runs in any frontend. Cancellation honor is impl-shaped: the
/// canonical Spectre/CLI impl checks the token before each prompt but cannot
/// interrupt a synchronous stdin read mid-prompt; a future Web UI impl can
/// honor cancellation fully.
/// </remarks>
public interface IActionPrompter
{
    Task<string> PromptTextAsync(
        string message,
        string? defaultValue = null,
        CancellationToken cancellationToken = default);

    Task<string> PromptSelectionAsync(
        string message,
        IReadOnlyList<string> choices,
        CancellationToken cancellationToken = default);

    Task<bool> PromptConfirmAsync(
        string message,
        bool defaultValue = false,
        CancellationToken cancellationToken = default);
}
