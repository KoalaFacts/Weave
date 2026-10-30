using System.Net;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
[Collection(nameof(ShellConsoleGroup))]
public sealed class MarketplaceInstallCancelledDiskTests
{
    [Fact]
    public Task ExecuteAsync_PreservesOwnedDiskState() =>
        MarketplaceInstallHttpScenarios.RunAsync(typeof(MarketplaceInstallCancelledDiskTests),
            () => MarketplaceInstallHttpScenarios.CancellationDuringInstall_ReachesHttpAndReturns130WithoutScaffolding());
}
