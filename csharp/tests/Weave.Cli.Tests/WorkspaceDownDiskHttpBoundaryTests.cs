using System.Net;

namespace Weave.Cli.Tests;

[Collection(nameof(ShellConsoleGroup))]
public sealed class WorkspaceDownDiskHttpBoundaryTests
{
    [Fact]
    public async Task SuccessfulStop_UsesDiskIdentityAndCapability_ThenDeletesOnlyState()
    {
        using var fixture = new WorkspaceDownDiskHttpFixture();
        using var output = new ShellOutputCapture();
        fixture.Handler.Respond = (_, _) =>
        {
            File.ReadAllText(fixture.StatePath).ShouldBe(WorkspaceDownDiskHttpFixture.StateText);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        };

        var result = await fixture.Command.ExecuteAsync(fixture.Options, TestContext.Current.CancellationToken);

        result.ShouldBe(0);
        fixture.AssertStopRequest();
        File.Exists(fixture.StatePath).ShouldBeFalse();
        fixture.AssertOtherFilesUnchanged();
        output.Text.ShouldContain("Workspace 'workspace-stop-42' stopped.");
        output.Text.ShouldNotContain(WorkspaceDownDiskHttpFixture.FakeCapability);
    }

    [Fact]
    public async Task ForbiddenStop_PreservesExactDiskState_AndReportsRefusal()
    {
        using var fixture = new WorkspaceDownDiskHttpFixture();
        using var output = new ShellOutputCapture();
        fixture.Handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));

        var result = await fixture.Command.ExecuteAsync(fixture.Options, TestContext.Current.CancellationToken);

        result.ShouldBe(1);
        fixture.AssertStopRequest();
        fixture.AssertStateUnchanged();
        output.Text.ShouldContain("Failed to stop workspace: Silo refused the stop request (403).");
        output.Text.ShouldNotContain("stopped.");
        output.Text.ShouldNotContain(WorkspaceDownDiskHttpFixture.FakeCapability);
    }

    [Fact]
    public async Task CancellationDuringHttpStop_ReachesHandler_Returns130AndPreservesState()
    {
        using var fixture = new WorkspaceDownDiskHttpFixture();
        using var output = new ShellOutputCapture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Respond = async (_, token) =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("An infinite delay must not complete normally.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                cancelled.TrySetResult();
                throw;
            }
        };

        var pending = fixture.Command.ExecuteAsync(fixture.Options, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            await cancellation.CancelAsync();
            // Drain the command even if the handshake times out, before disposing its HTTP client.
            await pending.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        result.ShouldBe(130);
        cancelled.Task.IsCompletedSuccessfully.ShouldBeTrue();
        fixture.AssertStopRequest();
        fixture.AssertStateUnchanged();
        output.Text.ShouldNotContain("stopped.");
        output.Text.ShouldNotContain(WorkspaceDownDiskHttpFixture.FakeCapability);
    }

    [Fact]
    public async Task MissingCapabilityFile_ReturnsFailureWithoutHttpOrStateDeletion()
    {
        using var fixture = new WorkspaceDownDiskHttpFixture();
        using var output = new ShellOutputCapture();
        var missingPath = Path.Join(fixture.Root, "missing-capability.txt");

        var result = await fixture.Command.ExecuteAsync(
            fixture.Options with { CapabilityFile = missingPath }, TestContext.Current.CancellationToken);

        result.ShouldBe(1);
        fixture.Handler.Calls.ShouldBe(0);
        fixture.AssertStateUnchanged();
        output.Text.ShouldContain("Could not read capability file:");
        output.Text.ShouldNotContain("stopped.");
    }

    [Fact]
    public async Task CapabilityFileContainingMultipleTokens_ReturnsFailureWithoutHttpOrStateDeletion()
    {
        using var fixture = new WorkspaceDownDiskHttpFixture();
        using var output = new ShellOutputCapture();
        var invalidPath = Path.Join(fixture.Root, "invalid-capability.txt");
        const string invalidText = "fake-first-token\nfake-second-token";
        File.WriteAllText(invalidPath, invalidText);

        var result = await fixture.Command.ExecuteAsync(
            fixture.Options with { CapabilityFile = invalidPath }, TestContext.Current.CancellationToken);

        result.ShouldBe(1);
        fixture.Handler.Calls.ShouldBe(0);
        fixture.AssertStateUnchanged();
        File.ReadAllText(invalidPath).ShouldBe(invalidText);
        output.Text.ShouldContain("The capability file must contain one encoded token.");
        output.Text.ShouldNotContain("fake-first-token");
        output.Text.ShouldNotContain("fake-second-token");
        output.Text.ShouldNotContain("stopped.");
    }
}
