using Weave.Actions.Context;
using Weave.Actions.Workspace;

namespace Weave.Actions.Tests.Workspace;

public sealed class OpenWorkspaceActionTests
{
    [Fact]
    public async Task ExecuteAsync_RequestedNameMatchesRegistered_ResolvesManifestPath()
    {
        var locator = new StubLocator(["alpha", "beta"], (_, name) => $"/ws/{name}/workspace.json");
        var prompter = new StubPrompter();
        var action = new OpenWorkspaceAction(locator, prompter);

        var result = await action.ExecuteAsync(new OpenWorkspaceInput("alpha"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Name.ShouldBe("alpha");
        result.Value.ManifestPath.ShouldBe("/ws/alpha/workspace.json");
        prompter.SelectionCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData("ALPHA")]
    [InlineData("Alpha")]
    [InlineData("alpha")]
    public async Task ExecuteAsync_RequestedNameMatchesCaseInsensitively(string requested)
    {
        var locator = new StubLocator(["alpha"], (_, name) => $"/ws/{name}/workspace.json");
        var action = new OpenWorkspaceAction(locator, new StubPrompter());

        var result = await action.ExecuteAsync(new OpenWorkspaceInput(requested), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Name.ShouldBe("alpha");
    }

    [Fact]
    public async Task ExecuteAsync_RequestedNameNotInRegistry_FallsThroughToLocator()
    {
        // Locator can still resolve a manifest from CWD even if the name
        // isn't registered (the CLI's ManifestResolver walks up the tree).
        var locator = new StubLocator([], (_, name) => name == "found-via-cwd" ? "/cwd/workspace.json" : null);
        var action = new OpenWorkspaceAction(locator, new StubPrompter());

        var result = await action.ExecuteAsync(new OpenWorkspaceInput("found-via-cwd"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Name.ShouldBe("found-via-cwd");
        result.Value.ManifestPath.ShouldBe("/cwd/workspace.json");
    }

    [Fact]
    public async Task ExecuteAsync_NoNameAndNoRegistered_ReturnsNotFoundWithCreateHint()
    {
        var locator = new StubLocator([], (_, _) => null);
        var action = new OpenWorkspaceAction(locator, new StubPrompter());

        var result = await action.ExecuteAsync(new OpenWorkspaceInput(null), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
        result.Failure.Reason.ShouldBe(ActionFailureReason.NotFound);
        result.Failure.Message.ShouldContain("workspace new");
    }

    [Fact]
    public async Task ExecuteAsync_NoNameAndRegistered_PromptsAndReturnsSelection()
    {
        var locator = new StubLocator(["alpha", "beta"], (_, name) => $"/ws/{name}/workspace.json");
        var prompter = new StubPrompter { Selection = "beta" };
        var action = new OpenWorkspaceAction(locator, prompter);

        var result = await action.ExecuteAsync(new OpenWorkspaceInput(null), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Name.ShouldBe("beta");
        prompter.SelectionCalls.ShouldBe(1);
        // The prompter receives every registered name plus a cancel choice.
        prompter.LastSelectionChoices.ShouldContain("alpha");
        prompter.LastSelectionChoices.ShouldContain("beta");
        prompter.LastSelectionChoices.ShouldContain("(cancel)");
    }

    [Fact]
    public async Task ExecuteAsync_PromptCancelChoice_ReturnsCancelled()
    {
        var locator = new StubLocator(["alpha"], (_, name) => $"/ws/{name}/workspace.json");
        var prompter = new StubPrompter { Selection = "(cancel)" };
        var action = new OpenWorkspaceAction(locator, prompter);

        var result = await action.ExecuteAsync(new OpenWorkspaceInput(null), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.Cancelled);
    }

    [Fact]
    public async Task ExecuteAsync_LocatorReturnsNull_ReturnsNotFound()
    {
        var locator = new StubLocator(["alpha"], (_, _) => null);
        var action = new OpenWorkspaceAction(locator, new StubPrompter());

        var result = await action.ExecuteAsync(new OpenWorkspaceInput("alpha"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.NotFound);
        result.Failure.Message.ShouldContain("alpha");
    }

    [Fact]
    public async Task ExecuteAsync_NullInput_Throws()
    {
        var action = new OpenWorkspaceAction(new StubLocator([], (_, _) => null), new StubPrompter());

        await Should.ThrowAsync<ArgumentNullException>(
            () => action.ExecuteAsync(null!, CancellationToken.None));
    }

    private sealed class StubLocator : IWorkspaceLocator
    {
        private readonly IReadOnlyList<string> _registered;
        private readonly Func<StubLocator, string?, string?> _resolve;

        public StubLocator(IReadOnlyList<string> registered, Func<StubLocator, string?, string?> resolve)
        {
            _registered = registered;
            _resolve = resolve;
        }

        public IReadOnlyList<string> RegisteredNames() => _registered;
        public string? ResolveManifestPath(string? name) => _resolve(this, name);
    }

    private sealed class StubPrompter : IActionPrompter
    {
        public string? Selection { get; set; } = "alpha";

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
