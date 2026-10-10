namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
[Collection(nameof(LocalHostProcessGroup))]
public sealed class LocalHostDeniedProcessTests
{
    [Fact]
    public Task RunAsync_ConnectDenied_DoesNotInitializeAndStopsOwnedWorker() =>
        SiloLauncherProcessHarness.RunAsync(typeof(LocalHostDeniedProcessTests),
            root => LocalHostProcessScenario.VerifyAsync(root, "denied"));
}
