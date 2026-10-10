namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
[Collection(nameof(LocalHostProcessGroup))]
public sealed class LocalHostUnreadyStoresProcessTests
{
    [Fact]
    public Task RunAsync_DurableStoresUnconfirmed_ReportsFailureAndStopsOwnedWorker() =>
        SiloLauncherProcessHarness.RunAsync(typeof(LocalHostUnreadyStoresProcessTests),
            root => LocalHostProcessScenario.VerifyAsync(root, "healthy", false));
}
