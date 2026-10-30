namespace Weave.Cli.Tests;

[Collection("Tui session console")]
public sealed class TuiSessionStateBoundaryTests
{
    [Theory]
    [InlineData("  workspace-42\n", "workspace-42", null)]
    [InlineData(" \n\t", null, "State file exists but is empty — workspace may have been interrupted.")]
    public void TryOpen_StateFile_ReflectsRunningStatusAndWarning(string state, string? expectedId, string? expectedWarning)
    {
        using var fixture = new TuiSessionBoundaryFixture();
        var path = fixture.AddWorkspace("research", """{"version":"1.0","name":"research"}""");
        TuiSessionBoundaryFixture.WriteState(path, state);
        fixture.Session.AgentName = "old-agent";

        var opened = fixture.Session.TryOpen("research", out var error);

        opened.ShouldBeTrue();
        error.ShouldBeNull();
        fixture.Session.WorkspaceName.ShouldBe("research");
        fixture.Session.ManifestPath.ShouldBe(path);
        fixture.Session.HasWorkspace.ShouldBeTrue();
        fixture.Session.WorkspaceId.ShouldBe(expectedId);
        fixture.Session.IsRunning.ShouldBe(expectedId is not null);
        fixture.Session.StateWarning.ShouldBe(expectedWarning);
        fixture.Session.AgentName.ShouldBeNull();
    }

    [Fact]
    public void TryOpen_LockedStateFile_TreatsWorkspaceAsStoppedAndExplainsReadFailure()
    {
        using var fixture = new TuiSessionBoundaryFixture();
        var path = fixture.AddWorkspace("research", """{"version":"1.0","name":"research"}""");
        TuiSessionBoundaryFixture.WriteState(path, "research-id");
        using var lockStream = File.Open(WorkspaceManifestPaths.GetStatePath(path), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var opened = fixture.Session.TryOpen("research", out var error);

        opened.ShouldBeTrue();
        error.ShouldBeNull();
        fixture.Session.HasWorkspace.ShouldBeTrue();
        fixture.Session.IsRunning.ShouldBeFalse();
        fixture.Session.WorkspaceId.ShouldBeNull();
        fixture.Session.StateWarning.ShouldNotBeNull();
        fixture.Session.StateWarning.ShouldEndWith(" — treating workspace as not running.");
    }

    [Fact]
    public async Task TryOpen_UnresolvableWorkspace_PreservesPreviousSession()
    {
        using var fixture = new TuiSessionBoundaryFixture();
        await fixture.SeedConversationAsync();
        var previousPath = fixture.Session.ManifestPath;

        var opened = fixture.Session.TryOpen("missing", out var error);

        opened.ShouldBeFalse();
        error.ShouldBe("No workspace.json found for 'missing'.");
        fixture.Session.WorkspaceName.ShouldBe("previous");
        fixture.Session.ManifestPath.ShouldBe(previousPath);
        fixture.Session.WorkspaceId.ShouldBe("previous-id");
        fixture.Session.AgentName.ShouldBe("previous-agent");
        fixture.Chat.History.Select(message => message.Content).ShouldBe(["retained question", "retained reply"]);
    }

    [Fact]
    public async Task ClearAgent_SelectedAgent_PreservesWorkspaceAndRunningState()
    {
        using var fixture = new TuiSessionBoundaryFixture();
        await fixture.SeedConversationAsync();

        fixture.Session.ClearAgent();

        fixture.Session.AgentName.ShouldBeNull();
        fixture.Session.WorkspaceName.ShouldBe("previous");
        fixture.Session.WorkspaceId.ShouldBe("previous-id");
        fixture.Session.HasWorkspace.ShouldBeTrue();
        fixture.Session.IsRunning.ShouldBeTrue();
    }

    [Fact]
    public async Task MarkStopped_RunningWorkspace_PreservesOpenWorkspaceAndAgent()
    {
        using var fixture = new TuiSessionBoundaryFixture();
        await fixture.SeedConversationAsync();

        fixture.Session.MarkStopped();

        fixture.Session.IsRunning.ShouldBeFalse();
        fixture.Session.WorkspaceId.ShouldBeNull();
        fixture.Session.HasWorkspace.ShouldBeTrue();
        fixture.Session.WorkspaceName.ShouldBe("previous");
        fixture.Session.AgentName.ShouldBe("previous-agent");
    }
}
