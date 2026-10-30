using Weave.Actions.Context;

namespace Weave.Actions.Agent;

/// <summary>
/// Phase 2 write verb. Resolves a (possibly null) agent name against a
/// frontend-supplied candidate list. Prompts the user via
/// <see cref="IActionPrompter"/> when no name is given; auto-selects the
/// only candidate when the list has exactly one entry and no name is
/// requested.
/// </summary>
public sealed class SelectAgentAction
{
    private const string CancelChoice = "(cancel)";

    private readonly IActionPrompter _prompter;

    public SelectAgentAction(IActionPrompter prompter)
    {
        _prompter = prompter;
    }

    public async Task<ActionResult<SelectAgentResult>> ExecuteAsync(
        SelectAgentInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Candidates);

        if (input.Candidates.Count == 0)
        {
            return ActionResult.Failed<SelectAgentResult>(ActionFailure.NotFound(
                "No agents available for this workspace."));
        }

        if (!string.IsNullOrWhiteSpace(input.RequestedName))
        {
            var match = input.Candidates
                .FirstOrDefault(n => string.Equals(n, input.RequestedName, StringComparison.OrdinalIgnoreCase));
            return match is null
                ? ActionResult.Failed<SelectAgentResult>(ActionFailure.NotFound(
                    $"Agent '{input.RequestedName}' not found."))
                : ActionResult.Success(new SelectAgentResult(match));
        }

        if (input.Candidates.Count == 1)
            return ActionResult.Success(new SelectAgentResult(input.Candidates[0]));

        var picked = await _prompter.PromptSelectionAsync(
            "Use which agent?",
            [.. input.Candidates, CancelChoice],
            cancellationToken);

        if (picked == CancelChoice)
            return ActionResult.Failed<SelectAgentResult>(ActionFailure.Cancelled());

        return ActionResult.Success(new SelectAgentResult(picked));
    }
}
