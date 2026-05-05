namespace Weave.Actions.Context;

/// <summary>
/// Asks the user for missing input. Frontend-supplied: the CLI uses Spectre
/// prompts; a Web UI rejects every prompt (web frontends pre-fill inputs);
/// test fakes return canned answers.
/// </summary>
/// <remarks>
/// Actions never instantiate prompts inline — they call this seam so the same
/// orchestration runs in any frontend.
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
