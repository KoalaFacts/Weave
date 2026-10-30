namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class RunOwnedWorkerRejectedProcessTests
{
    [Fact]
    public Task ExecuteAsync_OwnedWorkerRejectsStart_TerminatesChildAndPreservesState() =>
        SiloLauncherProcessHarness.RunAsync(typeof(RunOwnedWorkerRejectedProcessTests),
            root => SiloLifecycleCommands.VerifyRunAsync(root, true));
}
