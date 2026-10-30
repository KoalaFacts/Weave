namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class RunPendingStartCancellationProcessTests
{
    [Fact]
    public Task ExecuteAsync_CancelledPendingStart_StopsWithoutStateAndLeavesServerAlive() =>
        SiloLauncherProcessHarness.RunAsync(typeof(RunPendingStartCancellationProcessTests),
            root => RunCommandScenario.ExecuteAsync(root, "cancel-pending"));
}
