namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class WebUiExplicitHandoffProcessTests
{
    [Fact]
    public Task ExecuteAsync_ExplicitUrl_OverridesEnvironmentAndHandsOneArgumentToOwnedOpener() =>
        SiloLauncherProcessHarness.RunAsync(typeof(WebUiExplicitHandoffProcessTests),
            root => WebUiHandoffScenario.ExecuteAsync(root, "explicit"));
}
