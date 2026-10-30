using System.Net;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
[Collection(nameof(ShellConsoleGroup))]
public sealed class MarketplaceInstallEmptyCatalogDiskTests
{
    [Fact]
    public Task ExecuteAsync_PreservesOwnedDiskState() =>
        MarketplaceInstallHttpScenarios.RunAsync(typeof(MarketplaceInstallEmptyCatalogDiskTests),
            () => MarketplaceInstallHttpScenarios.NoItemSuppliedAndCatalogEmpty_DoesNotAttemptInstallOrPrompt());
}
