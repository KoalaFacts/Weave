namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class SiloLauncherEarlyExitProcessTests
{
    [Fact]
    public Task AutoStartServeWithDiagnosticsAsync_WorkerExitsBeforeHealth_ReturnsActualExitCode() =>
        SiloLauncherProcessHarness.RunAsync(typeof(SiloLauncherEarlyExitProcessTests), async root =>
        {
            await using var worker = new SiloLauncherFixtureWorker(root, earlyExit: true);
            var launcher = SiloLauncherProcessHarness.Create(new CliConfig { SiloPath = worker.Executable, DefaultPort = worker.Port });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            worker.ReleasePort();
            var launching = launcher.AutoStartServeWithDiagnosticsAsync(timeout.Token);
            await worker.ObserveStartedAsync(launching);
            worker.AllowExit();

            var result = await launching;

            result.Success.ShouldBeFalse();
            result.Reason.ShouldBe("Silo process exited with code 23 during startup.");
            result.LogPath.ShouldBe(Path.Join(root, ".weave", "silo.log"));
            File.Exists(result.LogPath).ShouldBeTrue();
            File.Exists(Path.Join(root, "health-requests")).ShouldBeFalse();
        });
}
