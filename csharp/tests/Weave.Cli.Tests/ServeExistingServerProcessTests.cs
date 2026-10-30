using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class ServeExistingServerProcessTests
{
    [Fact]
    public Task ExecuteAsync_HealthyOwnedEndpoint_DoesNotResolveOrLaunchAnotherServer() =>
        SiloLauncherProcessHarness.RunAsync(typeof(ServeExistingServerProcessTests), async _ =>
        {
            await using var server = new LocalCliLoopbackServer(new LocalCliHttpStep("GET", "/health", 200, "{}"));
            var launcher = new ServeBoundaryLauncher();
            using var output = new ShellOutputCapture();

            (await new ServeCliCommand(launcher).ExecuteAsync(new ServeOptions(server.Port, false),
                TestContext.Current.CancellationToken)).ShouldBe(0);
            await server.CompleteAsync();

            launcher.Resolutions.ShouldBe(0);
            output.Text.ShouldContain($"Weave is already running on port {server.Port}.");
            server.Headers.ShouldHaveSingleItem();
        });
}
