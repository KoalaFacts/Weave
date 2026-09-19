using Weave.Actions.Agent;
using Weave.Actions.Context;

namespace Weave.Actions.Tests.Agent;

public sealed class SelectAgentActionTests
{
    [Fact]
    public async Task ExecuteAsync_RequestedNameMatchesCandidate_ReturnsCanonicalCasing()
    {
        var prompter = new StubPrompter();
        var action = new SelectAgentAction(prompter);

        var result = await action.ExecuteAsync(
            new SelectAgentInput(["alpha", "beta"], "ALPHA"),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        // Returns the candidate's casing, not what the user typed.
        result.Value!.AgentName.ShouldBe("alpha");
        prompter.SelectionCalls.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_RequestedNameNotInCandidates_ReturnsNotFound()
    {
        var action = new SelectAgentAction(new StubPrompter());

        var result = await action.ExecuteAsync(
            new SelectAgentInput(["alpha"], "delta"),
            CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
        result.Failure.Reason.ShouldBe(ActionFailureReason.NotFound);
        result.Failure.Message.ShouldContain("delta");
    }

    [Fact]
    public async Task ExecuteAsync_NoRequestedNameSingleCandidate_AutoSelectsWithoutPrompt()
    {
        var prompter = new StubPrompter();
        var action = new SelectAgentAction(prompter);

        var result = await action.ExecuteAsync(
            new SelectAgentInput(["only-agent"], null),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.AgentName.ShouldBe("only-agent");
        prompter.SelectionCalls.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_NoRequestedNameMultipleCandidates_PromptsAndReturnsPick()
    {
        var prompter = new StubPrompter { Selection = "beta" };
        var action = new SelectAgentAction(prompter);

        var result = await action.ExecuteAsync(
            new SelectAgentInput(["alpha", "beta", "gamma"], null),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.AgentName.ShouldBe("beta");
        prompter.SelectionCalls.ShouldBe(1);
        prompter.LastSelectionChoices.ShouldContain("alpha");
        prompter.LastSelectionChoices.ShouldContain("beta");
        prompter.LastSelectionChoices.ShouldContain("gamma");
        prompter.LastSelectionChoices.ShouldContain("(cancel)");
    }

    [Fact]
    public async Task ExecuteAsync_PromptCancelChoice_ReturnsCancelled()
    {
        var prompter = new StubPrompter { Selection = "(cancel)" };
        var action = new SelectAgentAction(prompter);

        var result = await action.ExecuteAsync(
            new SelectAgentInput(["alpha", "beta"], null),
            CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.Cancelled);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyCandidateList_ReturnsNotFound()
    {
        var action = new SelectAgentAction(new StubPrompter());

        var result = await action.ExecuteAsync(
            new SelectAgentInput([], null),
            CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.NotFound);
        result.Failure.Message.ShouldContain("No agents");
    }

    [Fact]
    public async Task ExecuteAsync_NullInput_Throws()
    {
        var action = new SelectAgentAction(new StubPrompter());

        await Should.ThrowAsync<ArgumentNullException>(
            () => action.ExecuteAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_NullCandidates_Throws()
    {
        var action = new SelectAgentAction(new StubPrompter());

        await Should.ThrowAsync<ArgumentNullException>(
            () => action.ExecuteAsync(new SelectAgentInput(null!, null), CancellationToken.None));
    }

    private sealed class StubPrompter : IActionPrompter
    {
        public string? Selection { get; set; }

        public int SelectionCalls { get; private set; }

        public IReadOnlyList<string> LastSelectionChoices { get; private set; } = [];

        public Task<string> PromptTextAsync(string message, string? defaultValue = null, CancellationToken cancellationToken = default)
            => Task.FromResult(defaultValue ?? string.Empty);

        public Task<string> PromptSelectionAsync(string message, IReadOnlyList<string> choices, CancellationToken cancellationToken = default)
        {
            SelectionCalls++;
            LastSelectionChoices = choices;
            return Task.FromResult(Selection ?? choices[0]);
        }

        public Task<bool> PromptConfirmAsync(string message, bool defaultValue = false, CancellationToken cancellationToken = default)
            => Task.FromResult(defaultValue);
    }
}
