using System.Net;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
[Collection(nameof(ShellConsoleGroup))]
public sealed class MarketplaceInstallForbiddenDiskTests
{
    [Fact]
    public Task ExecuteAsync_PreservesOwnedDiskState() =>
        MarketplaceInstallHttpScenarios.RunAsync(typeof(MarketplaceInstallForbiddenDiskTests),
            () => MarketplaceInstallHttpScenarios.InstallRejected_ReturnsFailureWithoutScaffolding(HttpStatusCode.Forbidden, "Silo refused install: 403."));
}
