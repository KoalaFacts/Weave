namespace Weave.Actions.Context;

/// <summary>
/// Asks the user for missing input. Frontend-supplied: the CLI uses Spectre
/// prompts; a Web UI rejects every prompt (web frontends pre-fill inputs);
/// test fakes return canned answers.
/// </summary>
/// <remarks>
/// Actions never instantiate prompts inline — they call this seam so the same
/// orchestration runs in any frontend. No <c>CancellationToken</c> on these
/// methods: the canonical CLI impl wraps Spectre.Console which blocks
/// synchronously on stdin and cannot be cancelled mid-prompt. If a future
/// frontend wires up async prompting, add the parameter then.
/// </remarks>
public interface IActionPrompter
{
    Task<string> PromptTextAsync(string message, string? defaultValue = null);

    Task<string> PromptSelectionAsync(string message, IReadOnlyList<string> choices);

    Task<bool> PromptConfirmAsync(string message, bool defaultValue = false);
}
