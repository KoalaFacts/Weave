using System.Net;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
[Collection(nameof(ShellConsoleGroup))]
public sealed class MarketplaceInstallNotFoundDiskTests
{
    [Fact]
    public Task ExecuteAsync_PreservesOwnedDiskState() =>
        MarketplaceInstallHttpScenarios.RunAsync(typeof(MarketplaceInstallNotFoundDiskTests),
            () => MarketplaceInstallHttpScenarios.InstallRejected_ReturnsFailureWithoutScaffolding(HttpStatusCode.NotFound, "Item 'item?owned' not found."));
}
