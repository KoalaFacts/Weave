namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
[Collection(nameof(LocalHostProcessGroup))]
public sealed class LocalHostInitializationProcessTests
{
    [Fact]
    public Task RunAsync_ConnectedWorker_InitializesWithPrivateLaunchAndRedactedDiagnostics() =>
        SiloLauncherProcessHarness.RunAsync(typeof(LocalHostInitializationProcessTests),
            root => LocalHostProcessScenario.VerifyAsync(root, "healthy"));
}
