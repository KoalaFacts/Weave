namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class RunExistingServerProcessTests
{
    [Fact]
    public Task ExecuteAsync_ExistingServer_PersistsPreparedWorkspaceAndStopsOnCancellation() =>
        SiloLauncherProcessHarness.RunAsync(typeof(RunExistingServerProcessTests),
            root => RunCommandScenario.ExecuteAsync(root, "success"));
}
