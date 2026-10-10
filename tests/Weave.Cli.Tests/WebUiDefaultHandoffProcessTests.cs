namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class WebUiDefaultHandoffProcessTests
{
    [Fact]
    public Task ExecuteAsync_NoUrl_HandsConfiguredDashboardPortToOwnedOpener() =>
        SiloLauncherProcessHarness.RunAsync(typeof(WebUiDefaultHandoffProcessTests),
            root => WebUiHandoffScenario.ExecuteAsync(root, "configured-default"));
}
