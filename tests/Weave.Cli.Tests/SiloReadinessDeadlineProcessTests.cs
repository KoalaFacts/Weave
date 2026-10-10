namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class SiloReadinessDeadlineProcessTests
{
    [Fact]
    public Task WaitForReadyAsync_UnhealthyOrCancelled_RemainsBounded() =>
        SiloLauncherProcessHarness.RunAsync(typeof(SiloReadinessDeadlineProcessTests),
            root => SiloLifecycleConfiguration.VerifyReadinessAsync(root));
}
