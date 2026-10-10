namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class ServeForegroundExitProcessTests
{
    [Fact]
    public Task ExecuteAsync_ForegroundWorkerExits_PropagatesExitCode() =>
        SiloLauncherProcessHarness.RunAsync(typeof(ServeForegroundExitProcessTests),
            root => SiloLifecycleCommands.VerifyServeAsync(root, false));
}
