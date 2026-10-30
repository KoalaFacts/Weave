using System.Net;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
[Collection(nameof(ShellConsoleGroup))]
public sealed class MarketplaceInstallUnavailableDiskTests
{
    [Fact]
    public Task ExecuteAsync_PreservesOwnedDiskState() =>
        MarketplaceInstallHttpScenarios.RunAsync(typeof(MarketplaceInstallUnavailableDiskTests),
            () => MarketplaceInstallHttpScenarios.ServerUnavailable_StopsAtHealthProbe());
}
