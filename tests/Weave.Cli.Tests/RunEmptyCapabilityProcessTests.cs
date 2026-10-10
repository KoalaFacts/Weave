namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class RunEmptyCapabilityProcessTests
{
    [Fact]
    public Task ExecuteAsync_EmptyCapability_DoesNotContactServerOrWriteState() =>
        SiloLauncherProcessHarness.RunAsync(typeof(RunEmptyCapabilityProcessTests),
            root => RunCommandScenario.ExecuteAsync(root, "empty-capability"));
}
