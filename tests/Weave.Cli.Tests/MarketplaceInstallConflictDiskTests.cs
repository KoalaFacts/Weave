using System.Net;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
[Collection(nameof(ShellConsoleGroup))]
public sealed class MarketplaceInstallConflictDiskTests
{
    [Fact]
    public Task ExecuteAsync_PreservesOwnedDiskState() =>
        MarketplaceInstallHttpScenarios.RunAsync(typeof(MarketplaceInstallConflictDiskTests),
            () => MarketplaceInstallHttpScenarios.InstallRejected_ReturnsFailureWithoutScaffolding(HttpStatusCode.Conflict, "Install rejected: incompatible-template-marker"));
}
