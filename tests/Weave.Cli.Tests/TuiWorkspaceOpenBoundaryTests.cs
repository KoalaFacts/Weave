using Weave.Actions.Context;
using Weave.Actions.Workspace;
using Weave.Cli.Tui.Verbs;

namespace Weave.Cli.Tests;

[Collection("Tui session console")]
public sealed class TuiWorkspaceOpenBoundaryTests
{
    [Fact]
    public async Task DispatchAsync_ValidWorkspace_ReplacesSessionSelectsOnlyAgentAndClearsHistory()
    {
        using var fixture = new TuiSessionBoundaryFixture();
        await fixture.SeedConversationAsync();
        var path = fixture.AddWorkspace("research", """{"version":"1.0","workspace":{"isolation":"full"},"name":"research","agents":{"reviewer":{"model":"test-model","tools":[]}}}""");
        var locator = Substitute.For<IWorkspaceLocator>();
        locator.RegisteredNames().Returns(["research"]);
        locator.ResolveManifestPath("research").Returns(path);
        var dispatcher = new TuiSlashCommandDispatcher(fixture.Chat, [fixture.CreateOpener(locator)]);

        var result = await dispatcher.DispatchAsync(fixture.Session, "o RESEARCH", TestContext.Current.CancellationToken);

        result.ShouldBe(TuiDispatchResult.Continue);
        fixture.Session.WorkspaceName.ShouldBe("research");
        fixture.Session.ManifestPath.ShouldBe(path);
        fixture.Session.WorkspaceId.ShouldBeNull();
        fixture.Session.AgentName.ShouldBe("reviewer");
        fixture.Chat.History.ShouldBeEmpty();
        fixture.Output.ShouldContain("Workspace · research");
        fixture.Output.ShouldContain("/up");
    }

    [Theory]
    [InlineData("missing", false, "No workspace.json found for 'missing'.")]
    [InlineData(null, false, "No workspaces registered.")]
    [InlineData(null, true, null)]
    public async Task DispatchAsync_OpenFails_PreservesSessionAndHistory(string? requested, bool cancel, string? error)
    {
        using var fixture = new TuiSessionBoundaryFixture();
        await fixture.SeedConversationAsync();
        var locator = Substitute.For<IWorkspaceLocator>();
        locator.RegisteredNames().Returns(cancel ? ["research"] : Array.Empty<string>());
        var prompter = Substitute.For<IActionPrompter>();
        prompter.PromptSelectionAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns("(cancel)");
        var opener = fixture.CreateOpener(locator, prompter);

        await opener.DispatchAsync(new TuiVerbContext(fixture.Session, requested, fixture.Chat.Clear),
            TestContext.Current.CancellationToken);

        fixture.Session.WorkspaceName.ShouldBe("previous");
        fixture.Session.WorkspaceId.ShouldBe("previous-id");
        fixture.Session.AgentName.ShouldBe("previous-agent");
        fixture.Chat.History.Select(message => message.Content).ShouldBe(["retained question", "retained reply"]);
        if (error is null)
            fixture.Output.ShouldBeEmpty();
        else
            fixture.Output.ShouldContain(error);
    }

    [Fact]
    public async Task DispatchAsync_ResolverCannotReopenLocatedWorkspace_PreservesPreviousSession()
    {
        using var fixture = new TuiSessionBoundaryFixture();
        await fixture.SeedConversationAsync();
        var locator = Substitute.For<IWorkspaceLocator>();
        locator.RegisteredNames().Returns(["removed"]);
        locator.ResolveManifestPath("removed").Returns("removed/workspace.json");
        var opener = fixture.CreateOpener(locator);

        await opener.DispatchAsync(new TuiVerbContext(fixture.Session, "removed", fixture.Chat.Clear),
            TestContext.Current.CancellationToken);

        fixture.Session.WorkspaceName.ShouldBe("previous");
        fixture.Session.WorkspaceId.ShouldBe("previous-id");
        fixture.Session.AgentName.ShouldBe("previous-agent");
        fixture.Chat.History.Select(message => message.Content).ShouldBe(["retained question", "retained reply"]);
        fixture.Output.ShouldContain("No workspace.json found for 'removed'.");
    }

    [Fact]
    public async Task DispatchAsync_EmptyStateFile_ShowsWarningAndStartHint()
    {
        using var fixture = new TuiSessionBoundaryFixture();
        var path = fixture.AddWorkspace("research", """{"version":"1.0","workspace":{"isolation":"full"},"name":"research"}""");
        TuiSessionBoundaryFixture.WriteState(path, " \n");
        var locator = Substitute.For<IWorkspaceLocator>();
        locator.RegisteredNames().Returns(["research"]);
        locator.ResolveManifestPath("research").Returns(path);
        var opener = fixture.CreateOpener(locator);

        await opener.DispatchAsync(new TuiVerbContext(fixture.Session, "research", fixture.Chat.Clear),
            TestContext.Current.CancellationToken);

        fixture.Session.IsRunning.ShouldBeFalse();
        fixture.Session.StateWarning.ShouldBe("State file exists but is empty — workspace may have been interrupted.");
        fixture.Output.ShouldContain("State file exists but is empty");
        fixture.Output.ShouldContain("Workspace · research");
        fixture.Output.ShouldContain("/up");
    }

    [Fact]
    public async Task DispatchAsync_MalformedManifest_ExplainsParseFailureWithoutSelectingAgent()
    {
        using var fixture = new TuiSessionBoundaryFixture();
        await fixture.SeedConversationAsync();
        var path = fixture.AddWorkspace("broken", "{invalid manifest");
        var locator = Substitute.For<IWorkspaceLocator>();
        locator.RegisteredNames().Returns(["broken"]);
        locator.ResolveManifestPath("broken").Returns(path);
        var opener = fixture.CreateOpener(locator);

        await opener.DispatchAsync(new TuiVerbContext(fixture.Session, "broken", fixture.Chat.Clear),
            TestContext.Current.CancellationToken);

        fixture.Output.ShouldContain("Failed to parse manifest:");
        fixture.Output.ShouldNotContain("Workspace ·");
        fixture.Session.AgentName.ShouldBeNull();
    }
}
