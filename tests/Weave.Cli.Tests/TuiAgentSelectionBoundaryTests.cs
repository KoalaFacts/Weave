using Weave.Actions.Context;
using Weave.Cli.Tui.Verbs;

namespace Weave.Cli.Tests;

[Collection("Tui session console")]
public sealed class TuiAgentSelectionBoundaryTests
{
    private const string TwoAgents = """{"version":"1.0","name":"research","agents":{"reviewer":{"model":"test-model"},"writer":{"model":"test-model"}}}""";

    [Theory]
    [InlineData("use WRITER")]
    [InlineData("a WRITER")]
    public async Task DispatchAsync_NamedAgent_SelectsCanonicalNameAndClearsHistory(string command)
    {
        using var fixture = new TuiSessionBoundaryFixture();
        await fixture.SeedConversationAsync();
        fixture.AddWorkspace("research", TwoAgents);
        fixture.Session.TryOpen("research", out _).ShouldBeTrue();
        fixture.Session.AgentName = "reviewer";
        var dispatcher = new TuiSlashCommandDispatcher(fixture.Chat, [fixture.CreateSelector()]);

        var result = await dispatcher.DispatchAsync(fixture.Session, command, TestContext.Current.CancellationToken);

        result.ShouldBe(TuiDispatchResult.Continue);
        fixture.Session.AgentName.ShouldBe("writer");
        fixture.Chat.History.ShouldBeEmpty();
        fixture.Output.ShouldContain("Agent set to 'writer'.");
        fixture.Output.ShouldContain("/up");
    }

    [Fact]
    public async Task DispatchAsync_UnknownAgent_PreservesSelectionAndHistory()
    {
        using var fixture = new TuiSessionBoundaryFixture();
        await fixture.SeedConversationAsync();
        fixture.Session.MarkStopped();
        var selector = fixture.CreateSelector();

        await selector.DispatchAsync(new TuiVerbContext(fixture.Session, "missing-agent", fixture.Chat.Clear),
            TestContext.Current.CancellationToken);

        fixture.Session.AgentName.ShouldBe("previous-agent");
        fixture.Chat.History.Select(message => message.Content).ShouldBe(["retained question", "retained reply"]);
        fixture.Output.ShouldContain("Agent 'missing-agent' not found in 'previous'.");
        fixture.Output.ShouldNotContain("Agent set to");
    }

    [Fact]
    public async Task DispatchAsync_CancelSelection_PreservesSelectionAndHistoryWithoutError()
    {
        using var fixture = new TuiSessionBoundaryFixture();
        await fixture.SeedConversationAsync();
        fixture.AddWorkspace("research", TwoAgents);
        fixture.Session.TryOpen("research", out _).ShouldBeTrue();
        fixture.Session.AgentName = "reviewer";
        var prompter = Substitute.For<IActionPrompter>();
        prompter.PromptSelectionAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns("(cancel)");
        var selector = fixture.CreateSelector(prompter);

        await selector.DispatchAsync(new TuiVerbContext(fixture.Session, null, fixture.Chat.Clear),
            TestContext.Current.CancellationToken);

        fixture.Session.AgentName.ShouldBe("reviewer");
        fixture.Chat.History.Select(message => message.Content).ShouldBe(["retained question", "retained reply"]);
        fixture.Output.ShouldBeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_NoWorkspace_ShowsOpenHintAndPreservesHistory()
    {
        using var fixture = new TuiSessionBoundaryFixture();
        await fixture.SeedConversationAsync();
        var unopened = fixture.CreateUnopenedSession();

        await fixture.CreateSelector().DispatchAsync(new TuiVerbContext(unopened, "writer", fixture.Chat.Clear),
            TestContext.Current.CancellationToken);

        unopened.HasWorkspace.ShouldBeFalse();
        unopened.AgentName.ShouldBeNull();
        fixture.Chat.History.Select(message => message.Content).ShouldBe(["retained question", "retained reply"]);
        fixture.Output.ShouldContain("No workspace open. Try: /open <workspace>");
    }

    [Fact]
    public async Task DispatchAsync_EmptyAgentManifest_ShowsNoAgentsAndPreservesHistory()
    {
        using var fixture = new TuiSessionBoundaryFixture();
        await fixture.SeedConversationAsync();
        fixture.AddWorkspace("empty", """{"version":"1.0","name":"empty"}""");
        fixture.Session.TryOpen("empty", out _).ShouldBeTrue();

        await fixture.CreateSelector().DispatchAsync(new TuiVerbContext(fixture.Session, null, fixture.Chat.Clear),
            TestContext.Current.CancellationToken);

        fixture.Session.AgentName.ShouldBeNull();
        fixture.Chat.History.Select(message => message.Content).ShouldBe(["retained question", "retained reply"]);
        fixture.Output.ShouldContain("No agents available for this workspace.");
    }

    [Fact]
    public void TrySelectOnlyAgent_MultipleAgents_DoesNotChooseArbitrarily()
    {
        using var fixture = new TuiSessionBoundaryFixture();
        fixture.AddWorkspace("research", TwoAgents);
        fixture.Session.TryOpen("research", out _).ShouldBeTrue();

        fixture.CreateSelector().TrySelectOnlyAgent(fixture.Session);

        fixture.Session.AgentName.ShouldBeNull();
        fixture.Output.ShouldBeEmpty();
    }

    [Fact]
    public void TrySelectOnlyAgent_MalformedManifest_PreservesSelectionAndExplainsFailure()
    {
        using var fixture = new TuiSessionBoundaryFixture();
        fixture.AddWorkspace("broken", "{invalid manifest");
        fixture.Session.TryOpen("broken", out _).ShouldBeTrue();
        fixture.Session.AgentName = "retained-agent";

        fixture.CreateSelector().TrySelectOnlyAgent(fixture.Session);

        fixture.Session.AgentName.ShouldBe("retained-agent");
        fixture.Output.ShouldContain("Could not auto-select agent (");
    }
}
