using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class ServeMissingHostProcessTests
{
    [Fact]
    public Task ExecuteAsync_UnhealthyEndpointAndMissingHost_ReportsSetupGuidanceWithoutClaimingStartup() =>
        SiloLauncherProcessHarness.RunAsync(typeof(ServeMissingHostProcessTests), async _ =>
        {
            await using var server = new LocalCliLoopbackServer(new LocalCliHttpStep("GET", "/health", 503, "{}"));
            var launcher = new ServeBoundaryLauncher();
            using var output = new ShellOutputCapture();

            (await new ServeCliCommand(launcher).ExecuteAsync(new ServeOptions(server.Port, true),
                TestContext.Current.CancellationToken)).ShouldBe(1);
            await server.CompleteAsync();

            launcher.Resolutions.ShouldBe(1);
            output.Text.ShouldContain("Could not locate the Weave silo.");
            output.Text.ShouldContain("weave init");
            output.Text.ShouldNotContain("running in background");
            server.Headers.ShouldHaveSingleItem();
        });
}
