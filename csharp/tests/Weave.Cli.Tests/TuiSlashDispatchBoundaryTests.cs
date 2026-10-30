using Weave.Cli.Tui.Verbs;

namespace Weave.Cli.Tests;

[Collection("Tui session console")]
public sealed class TuiSlashDispatchBoundaryTests
{
    [Theory]
    [InlineData("OpEn")]
    [InlineData("o")]
    public async Task DispatchAsync_RegisteredVerb_ForwardsContextAndTokenAndClearsRealHistory(string name)
    {
        using var fixture = new TuiSessionBoundaryFixture();
        await fixture.SeedConversationAsync();
        using var cancellation = new CancellationTokenSource();
        var verb = new RecordingVerb();
        var dispatcher = new TuiSlashCommandDispatcher(fixture.Chat, [verb]);

        var result = await dispatcher.DispatchAsync(fixture.Session, $"{name}  Report  Notes  ", cancellation.Token);

        result.ShouldBe(TuiDispatchResult.Continue);
        verb.Context.ShouldNotBeNull();
        verb.Context.Session.ShouldBeSameAs(fixture.Session);
        verb.Context.Args.ShouldBe("Report  Notes");
        verb.Token.ShouldBe(cancellation.Token);
        fixture.Chat.History.ShouldBeEmpty();
        fixture.Session.WorkspaceId.ShouldBe("previous-id");
        fixture.Session.AgentName.ShouldBe("previous-agent");
    }

    [Theory]
    [InlineData("quit")]
    [InlineData("exit")]
    [InlineData("q")]
    public async Task DispatchAsync_QuitCommand_ReturnsQuitWithoutClearingSession(string command)
    {
        using var fixture = new TuiSessionBoundaryFixture();
        await fixture.SeedConversationAsync();
        var dispatcher = new TuiSlashCommandDispatcher(fixture.Chat, []);

        var result = await dispatcher.DispatchAsync(fixture.Session, command, TestContext.Current.CancellationToken);

        result.ShouldBe(TuiDispatchResult.Quit);
        fixture.Session.AgentName.ShouldBe("previous-agent");
        fixture.Chat.History.Select(message => message.Content).ShouldBe(["retained question", "retained reply"]);
        fixture.Output.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("hlep", "Unknown command: /hlep. Did you mean /help?  Type /help for all commands.")]
    [InlineData("zzzzzzzz", "Unknown command: /zzzzzzzz. Type /help for all commands.")]
    public async Task DispatchAsync_UnknownCommand_ShowsUsefulErrorWithoutChangingConversation(string command, string message)
    {
        using var fixture = new TuiSessionBoundaryFixture();
        await fixture.SeedConversationAsync();
        var dispatcher = new TuiSlashCommandDispatcher(fixture.Chat, []);

        var result = await dispatcher.DispatchAsync(fixture.Session, command, TestContext.Current.CancellationToken);

        result.ShouldBe(TuiDispatchResult.Continue);
        fixture.Output.ShouldContain(message);
        fixture.Session.AgentName.ShouldBe("previous-agent");
        fixture.Chat.History.Select(entry => entry.Content).ShouldBe(["retained question", "retained reply"]);
    }

    [Fact]
    public async Task DispatchAsync_EmptyCommand_ContinuesWithoutChangingConversation()
    {
        using var fixture = new TuiSessionBoundaryFixture();
        await fixture.SeedConversationAsync();
        var dispatcher = new TuiSlashCommandDispatcher(fixture.Chat, []);

        var result = await dispatcher.DispatchAsync(fixture.Session, "   ", TestContext.Current.CancellationToken);

        result.ShouldBe(TuiDispatchResult.Continue);
        fixture.Output.ShouldBeEmpty();
        fixture.Chat.History.Select(entry => entry.Content).ShouldBe(["retained question", "retained reply"]);
    }

    [Fact]
    public async Task DispatchAsync_History_RendersRetainedConversationWithoutClearingIt()
    {
        using var fixture = new TuiSessionBoundaryFixture();
        await fixture.SeedConversationAsync();
        var dispatcher = new TuiSlashCommandDispatcher(fixture.Chat, []);

        var result = await dispatcher.DispatchAsync(fixture.Session, "history", TestContext.Current.CancellationToken);

        result.ShouldBe(TuiDispatchResult.Continue);
        fixture.Output.ShouldContain("History · previous-agent");
        fixture.Output.ShouldContain("retained question");
        fixture.Output.ShouldContain("retained reply");
        fixture.Chat.History.Select(entry => entry.Content).ShouldBe(["retained question", "retained reply"]);
    }

    [Theory]
    [InlineData("help")]
    [InlineData("?")]
    public async Task DispatchAsync_Help_ExplainsAgentSelectionLifecycleAndExit(string command)
    {
        using var fixture = new TuiSessionBoundaryFixture();
        var dispatcher = new TuiSlashCommandDispatcher(fixture.Chat, []);

        var result = await dispatcher.DispatchAsync(fixture.Session, command, TestContext.Current.CancellationToken);

        result.ShouldBe(TuiDispatchResult.Continue);
        fixture.Output.ShouldContain("/open <ws>");
        fixture.Output.ShouldContain("/use <agent>");
        fixture.Output.ShouldContain("/up [file]");
        fixture.Output.ShouldContain("optional capability file");
        fixture.Output.ShouldContain("/exit, /q, quit");
        fixture.Output.ShouldContain("anything without a leading slash is sent to the current agent");
    }

    [Theory]
    [InlineData("new")]
    [InlineData("n")]
    public async Task DispatchAsync_NewWorkspace_ExplainsExternalCreationWithoutOpeningWorkspace(string command)
    {
        using var fixture = new TuiSessionBoundaryFixture();
        var dispatcher = new TuiSlashCommandDispatcher(fixture.Chat, []);

        var result = await dispatcher.DispatchAsync(fixture.Session, command, TestContext.Current.CancellationToken);

        result.ShouldBe(TuiDispatchResult.Continue);
        fixture.Output.ShouldContain("weave workspace new <name>");
        fixture.Output.ShouldContain("--preset <preset>");
        fixture.Output.ShouldContain("register with the registry");
        fixture.Session.HasWorkspace.ShouldBeFalse();
    }

    private sealed class RecordingVerb : ITuiVerb
    {
        public string Name => "open";
        public IReadOnlyList<string> Aliases => ["o"];
        public TuiVerbContext? Context { get; private set; }
        public CancellationToken Token { get; private set; }

        public Task DispatchAsync(TuiVerbContext context, CancellationToken ct)
        {
            Context = context;
            Token = ct;
            context.ClearChatHistory();
            return Task.CompletedTask;
        }
    }
}
