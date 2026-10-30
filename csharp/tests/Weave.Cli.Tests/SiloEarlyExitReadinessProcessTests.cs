namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class SiloEarlyExitReadinessProcessTests
{
    [Fact]
    public Task WaitForReadyAsync_WorkerExitsAfterUnhealthyResponse_DoesNotReportReady() =>
        SiloLauncherProcessHarness.RunAsync(typeof(SiloEarlyExitReadinessProcessTests),
            root => SiloLifecycleConfiguration.VerifyEarlyExitAsync(root));
}
