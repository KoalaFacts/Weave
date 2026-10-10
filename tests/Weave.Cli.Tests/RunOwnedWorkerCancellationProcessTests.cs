namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class RunOwnedWorkerCancellationProcessTests
{
    [Fact]
    public Task ExecuteAsync_OwnedWorkerRunning_CancellationTerminatesChild() =>
        SiloLauncherProcessHarness.RunAsync(typeof(RunOwnedWorkerCancellationProcessTests),
            root => SiloLifecycleCommands.VerifyRunAsync(root, false));
}
