using System.Text.Json;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class SiloLauncherHealthyProcessTests
{
    [Fact]
    public Task AutoStartServeWithDiagnosticsAsync_WorkerBecomesHealthy_ReturnsSuccessAndCapturesBothStreams() =>
        SiloLauncherProcessHarness.RunAsync(typeof(SiloLauncherHealthyProcessTests), async root =>
        {
            await using var worker = new SiloLauncherFixtureWorker(root, earlyExit: false);
            var launcher = SiloLauncherProcessHarness.Create(new CliConfig { SiloPath = worker.Executable, DefaultPort = worker.Port });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            worker.ReleasePort();
            var launching = launcher.AutoStartServeWithDiagnosticsAsync(timeout.Token);
            await worker.ObserveStartedAsync(launching);

            var result = await launching;

            result.Success.ShouldBeTrue(result.Reason);
            result.Reason.ShouldBeNull();
            result.LogPath.ShouldBe(Path.Join(root, ".weave", "silo.log"));
            var requests = await File.ReadAllLinesAsync(Path.Join(root, "health-requests"), timeout.Token);
            requests.Length.ShouldBeGreaterThanOrEqualTo(2);
            requests.ShouldAllBe(request => request == "/health");
            using var arguments = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Join(root, "worker.args"), timeout.Token));
            arguments.RootElement.EnumerateArray().Select(argument => argument.GetString()).ToArray()
                .ShouldBe(["--Weave:LocalMode=true", $"--urls=http://localhost:{worker.Port}"]);
            var log = await File.ReadAllTextAsync(result.LogPath, timeout.Token);
            while (!log.Contains("OUT launcher-stdout-marker", StringComparison.Ordinal)
                || !log.Contains("ERR launcher-stderr-marker", StringComparison.Ordinal))
            {
                await Task.Delay(20, timeout.Token);
                log = await File.ReadAllTextAsync(result.LogPath, timeout.Token);
            }
            log.ShouldContain("OUT launcher-stdout-marker");
            log.ShouldContain("ERR launcher-stderr-marker");
        });
}
