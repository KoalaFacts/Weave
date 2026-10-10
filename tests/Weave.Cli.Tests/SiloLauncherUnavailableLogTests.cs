using System.Globalization;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class SiloLauncherUnavailableLogTests
{
    [Fact]
    public Task AutoStartServeWithDiagnosticsAsync_LogPathIsDirectory_ReportsDiagnosticAndPreservesPathWhileCheckingHealth() =>
        SiloLauncherProcessHarness.RunAsync(typeof(SiloLauncherUnavailableLogTests), async root =>
        {
            await using var worker = new SiloLauncherFixtureWorker(root, earlyExit: false);
            var logPath = Directory.CreateDirectory(Path.Join(root, ".weave", "silo.log")).FullName;
            var preservedPath = Path.Join(logPath, "preserved-marker");
            await File.WriteAllTextAsync(preservedPath, "preserved diagnostic folder", TestContext.Current.CancellationToken);
            var launcher = SiloLauncherProcessHarness.Create(new CliConfig { SiloPath = worker.Executable, DefaultPort = worker.Port });
            using var errors = new StringWriter(CultureInfo.InvariantCulture);
            var previousError = Console.Error;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            worker.ReleasePort();
            try
            {
                Console.SetError(errors);
                var launching = launcher.AutoStartServeWithDiagnosticsAsync(timeout.Token);
                await worker.ObserveStartedAsync(launching);
                var result = await launching;

                result.Success.ShouldBeTrue(result.Reason);
                result.Reason.ShouldBeNull();
                result.LogPath.ShouldBe(logPath);
                errors.ToString().ShouldContain("silo log unavailable:");
                errors.ToString().ShouldContain(logPath);
                Directory.Exists(logPath).ShouldBeTrue();
                (await File.ReadAllTextAsync(preservedPath, timeout.Token)).ShouldBe("preserved diagnostic folder");
                var requests = await File.ReadAllLinesAsync(Path.Join(root, "health-requests"), timeout.Token);
                requests.Length.ShouldBeGreaterThanOrEqualTo(2);
                requests.ShouldAllBe(request => request == "/health");
            }
            finally
            {
                Console.SetError(previousError);
            }
        });
}
