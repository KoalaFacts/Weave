namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class RunMissingHostProcessTests
{
    [Fact]
    public Task ExecuteAsync_UnhealthyServerAndMissingHost_ReturnsFailureWithoutState() =>
        SiloLauncherProcessHarness.RunAsync(typeof(RunMissingHostProcessTests),
            root => RunCommandScenario.ExecuteAsync(root, "missing-host"));
}
