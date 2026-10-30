namespace Weave.Cli.Tests;

[Collection("Tui session console")]
public sealed class TuiNextStepBoundaryTests
{
    [Fact]
    public void Render_NoWorkspace_DoesNotSuggestStartingOrSendingMessages()
    {
        using var fixture = new TuiSessionBoundaryFixture();

        TuiNextStepHint.Render(fixture.Session);

        fixture.Output.ShouldBeEmpty();
    }

    [Fact]
    public void Render_StoppedWorkspace_ShowsStartCommandAndEscapesWorkspaceName()
    {
        using var fixture = new TuiSessionBoundaryFixture();
        fixture.AddWorkspace("research[blue]", """{"version":"1.0","name":"research[blue]"}""");
        fixture.Session.TryOpen("research[blue]", out _).ShouldBeTrue();

        TuiNextStepHint.Render(fixture.Session);

        fixture.Output.ShouldContain("workspace is not running");
        fixture.Output.ShouldContain("/up");
        fixture.Output.ShouldContain("starts 'research[blue]' via the running Silo");
        fixture.Output.ShouldNotContain("Ready.");
    }

    [Fact]
    public void Render_RunningWithoutAgent_ShowsAgentSelectionCommands()
    {
        using var fixture = new TuiSessionBoundaryFixture();
        fixture.AddWorkspace("research", """{"version":"1.0","name":"research"}""");
        fixture.Session.TryOpen("research", out _).ShouldBeTrue();
        fixture.Session.MarkRunning("research-id");

        TuiNextStepHint.Render(fixture.Session);

        fixture.Output.ShouldContain("pick an agent with /agents or /use <name>");
        fixture.Output.ShouldNotContain("workspace is not running");
        fixture.Output.ShouldNotContain("Ready.");
    }

    [Fact]
    public void Render_RunningSelectedAgent_ShowsEscapedRecipient()
    {
        using var fixture = new TuiSessionBoundaryFixture();
        fixture.AddWorkspace("research", """{"version":"1.0","name":"research"}""");
        fixture.Session.TryOpen("research", out _).ShouldBeTrue();
        fixture.Session.MarkRunning("research-id");
        fixture.Session.AgentName = "reviewer[blue]";

        TuiNextStepHint.Render(fixture.Session);

        fixture.Output.ShouldContain("Ready. Type any message to send it to reviewer[blue]");
        fixture.Output.ShouldContain("/agents to switch");
        fixture.Output.ShouldNotContain("/up");
    }
}
