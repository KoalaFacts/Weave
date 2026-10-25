namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class SiloLauncherDiagnosticWriteFailureTests
{
    [Fact]
    public async Task ObserveStartedAsync_LifecyclePathIsADirectory_PreservesOriginalExitAndObservationFailure()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Silo launcher process fixtures require Linux HOME isolation and executable scripts.");
        static async Task Scenario(string root)
        {
            await using var worker = new SiloLauncherFixtureWorker(root, earlyExit: true);
            Directory.CreateDirectory(Path.Join(root, "worker.lifecycle"));
            var launcher = SiloLauncherProcessHarness.Create(new CliConfig { SiloPath = worker.Executable, DefaultPort = worker.Port });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            worker.ReleasePort();
            worker.AllowExit();
            var launching = launcher.AutoStartServeWithDiagnosticsAsync(timeout.Token);
            var result = await launching;
            result.Reason.ShouldBe("Silo process exited with code 23 during startup.");
            await worker.ObserveStartedAsync(launching);
        }
        if (Environment.GetEnvironmentVariable("WEAVE_TEST_SILO_LAUNCH_ROOT") is not null)
        {
            await SiloLauncherProcessHarness.RunAsync(typeof(SiloLauncherDiagnosticWriteFailureTests), Scenario);
            return;
        }
        var failure = await Record.ExceptionAsync(() => SiloLauncherProcessHarness.RunAsync(typeof(SiloLauncherDiagnosticWriteFailureTests), Scenario));
        failure.ShouldBeOfType<ShouldAssertException>();
        failure.Message.ShouldContain("Reason=Silo process exited with code 23 during startup.");
        failure.Message.ShouldContain("ArgumentException");
        failure.Message.ShouldContain("Lifecycle evidence write failed:");
        failure.Message.ShouldContain("fixture lifecycle write failed:");
    }
}
