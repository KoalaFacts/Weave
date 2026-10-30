namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class SiloUnavailableLogProcessTests
{
    [Fact]
    public Task StartSilo_LogFileUnavailable_DrainsBothPipesUntilWorkerReady() =>
        SiloLauncherProcessHarness.RunAsync(typeof(SiloUnavailableLogProcessTests),
            root => SiloLifecycleConfiguration.VerifyLogFailureAsync(root));
}
