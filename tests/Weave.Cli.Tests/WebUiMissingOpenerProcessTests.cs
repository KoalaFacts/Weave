namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class WebUiMissingOpenerProcessTests
{
    [Fact]
    public Task ExecuteAsync_OpenerMissing_ShowsManualVisitFallbackWithoutSuccessClaim() =>
        SiloLauncherProcessHarness.RunAsync(typeof(WebUiMissingOpenerProcessTests),
            root => WebUiHandoffScenario.ExecuteAsync(root, "missing-opener"));
}
