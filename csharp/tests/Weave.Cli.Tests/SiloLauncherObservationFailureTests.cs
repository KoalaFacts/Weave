using System.Globalization;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class SiloLauncherObservationFailureTests
{
    [Fact]
    public async Task ObserveStartedAsync_WorkerAlreadyExited_PreservesFailureEvidenceAfterCleanup()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Silo launcher process fixtures require Linux HOME isolation and executable scripts.");
        static async Task Scenario(string root)
        {
            await using var worker = new SiloLauncherFixtureWorker(root, earlyExit: true);
            var launcher = SiloLauncherProcessHarness.Create(new CliConfig { SiloPath = worker.Executable, DefaultPort = worker.Port });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            worker.ReleasePort();
            worker.AllowExit();
            var launching = launcher.AutoStartServeWithDiagnosticsAsync(timeout.Token);
            var result = await launching;
            result.Success.ShouldBeFalse();
            result.Reason.ShouldBe("Silo process exited with code 23 during startup.");
            // Observe only after the real launcher has confirmed and reaped the worker's exit.
            await worker.ObserveStartedAsync(launching);
        }

        if (Environment.GetEnvironmentVariable("WEAVE_TEST_SILO_LAUNCH_ROOT") is not null)
        {
            await SiloLauncherProcessHarness.RunAsync(typeof(SiloLauncherObservationFailureTests), Scenario);
            return;
        }

        var failure = await Record.ExceptionAsync(() =>
            SiloLauncherProcessHarness.RunAsync(typeof(SiloLauncherObservationFailureTests), Scenario));
        failure.ShouldBeOfType<ShouldAssertException>();
        failure.Message.ShouldContain("Launcher result: Success=False; Reason=Silo process exited with code 23 during startup.");
        failure.Message.ShouldContain("ArgumentException");
        failure.Message.ShouldContain("ERR launcher-stderr-marker");
        var evidencePath = failure.Message.Split('\n').Select(line => line.Trim()).Single(line => line.StartsWith("Launcher fixture evidence: ", StringComparison.Ordinal))
            ["Launcher fixture evidence: ".Length..].Trim();
        File.Exists(evidencePath).ShouldBeTrue();
        var artifactRoot = Directory.GetParent(evidencePath).ShouldNotBeNull().Parent.ShouldNotBeNull().Parent.ShouldNotBeNull();
        artifactRoot.Name.ShouldBe("TestResults");
        File.Exists(Path.Join(artifactRoot.Parent.ShouldNotBeNull().FullName, "Weave.slnx")).ShouldBeTrue();
        var evidence = await File.ReadAllTextAsync(evidencePath, TestContext.Current.CancellationToken);
        evidence.ShouldContain("OUT launcher-stdout-marker");
        evidence.ShouldContain("ERR launcher-stderr-marker");
        evidence.ShouldContain("worker-exit=23");
        evidence.ShouldContain("Test host exit: 1");
        var rootPath = evidence.Split('\n').Single(line => line.StartsWith("Root: ", StringComparison.Ordinal))["Root: ".Length..].Trim();
        Directory.Exists(rootPath).ShouldBeFalse();
        var group = int.Parse(evidence.Split('\n').Single(line => line.StartsWith("Group: ", StringComparison.Ordinal))["Group: ".Length..], CultureInfo.InvariantCulture);
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var pid))
                continue;
            var fields = SiloLauncherProcessGroup.ReadProcessFields(pid, File.ReadAllText);
            if (fields is not null && int.Parse(fields[2], CultureInfo.InvariantCulture) == group)
                fields[0].ShouldBeOneOf("Z", "X");
        }
    }
}
