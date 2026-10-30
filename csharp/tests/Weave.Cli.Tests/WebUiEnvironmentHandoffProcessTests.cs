namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class WebUiEnvironmentHandoffProcessTests
{
    [Fact]
    public Task ExecuteAsync_NoExplicitUrl_HandsEnvironmentUrlToOwnedOpener() =>
        SiloLauncherProcessHarness.RunAsync(typeof(WebUiEnvironmentHandoffProcessTests),
            root => WebUiHandoffScenario.ExecuteAsync(root, "environment"));
}
