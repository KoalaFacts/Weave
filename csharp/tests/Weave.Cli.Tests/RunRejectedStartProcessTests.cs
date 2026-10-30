namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class RunRejectedStartProcessTests
{
    [Fact]
    public Task ExecuteAsync_ServerRejectsStart_PreservesPriorStateAndServer() =>
        SiloLauncherProcessHarness.RunAsync(typeof(RunRejectedStartProcessTests),
            root => RunCommandScenario.ExecuteAsync(root, "rejected"));
}
