using Weave.Actions.Context;

namespace Weave.Actions.Workspace;

/// <summary>
/// Phase 2 write verb. Resolves a (possibly null) workspace name into a
/// (canonical name, manifest path) pair via the frontend-supplied
/// <see cref="IWorkspaceLocator"/>.
/// </summary>
/// <remarks>
/// Prompts the user for a selection through <see cref="IActionPrompter"/>
/// when the name is missing. Frontends use the result to open their
/// session / load the manifest; the action itself doesn't read the
/// manifest body.
/// </remarks>
public sealed class OpenWorkspaceAction
{
    private const string CancelChoice = "(cancel)";

    private readonly IWorkspaceLocator _locator;
    private readonly IActionPrompter _prompter;

    public OpenWorkspaceAction(IWorkspaceLocator locator, IActionPrompter prompter)
    {
        _locator = locator;
        _prompter = prompter;
    }

    public async Task<ActionResult<OpenWorkspaceResult>> ExecuteAsync(
        OpenWorkspaceInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var requested = input.WorkspaceName;
        if (string.IsNullOrWhiteSpace(requested))
        {
            var names = _locator.RegisteredNames();
            if (names.Count == 0)
            {
                return ActionResult.Failed<OpenWorkspaceResult>(ActionFailure.NotFound(
                    "No workspaces registered. Create one with 'weave workspace new'."));
            }

            var picked = await _prompter.PromptSelectionAsync(
                "Open which workspace?",
                [.. names, CancelChoice],
                cancellationToken);

            if (picked == CancelChoice)
                return ActionResult.Failed<OpenWorkspaceResult>(ActionFailure.Cancelled());

            requested = picked;
        }
        else
        {
            // Case-insensitive match against the registered set so users can
            // type 'My-Workspace' even if it was registered as 'my-workspace'.
            requested = _locator.RegisteredNames()
                .FirstOrDefault(n => string.Equals(n, requested, StringComparison.OrdinalIgnoreCase))
                ?? requested;
        }

        var manifestPath = _locator.ResolveManifestPath(requested);
        if (manifestPath is null)
        {
            return ActionResult.Failed<OpenWorkspaceResult>(ActionFailure.NotFound(
                $"No workspace.json found for '{requested}'."));
        }

        return ActionResult.Success(new OpenWorkspaceResult(requested, manifestPath));
    }
}
