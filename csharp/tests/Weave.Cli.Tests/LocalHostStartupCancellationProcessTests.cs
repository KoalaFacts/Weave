namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
[Collection(nameof(LocalHostProcessGroup))]
public sealed class LocalHostStartupCancellationProcessTests
{
    [Fact]
    public Task RunAsync_CancelWhileConnectPending_DoesNotInitializeAndStopsOwnedWorker() =>
        SiloLauncherProcessHarness.RunAsync(typeof(LocalHostStartupCancellationProcessTests),
            root => LocalHostProcessScenario.VerifyAsync(root, "pending"));
}
