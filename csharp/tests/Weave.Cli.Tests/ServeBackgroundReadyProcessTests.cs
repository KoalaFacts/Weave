namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class ServeBackgroundReadyProcessTests
{
    [Fact]
    public Task ExecuteAsync_BackgroundWorkerReady_ReturnsWhileOwnedWorkerLives() =>
        SiloLauncherProcessHarness.RunAsync(typeof(ServeBackgroundReadyProcessTests),
            root => SiloLifecycleCommands.VerifyServeAsync(root, true));
}
