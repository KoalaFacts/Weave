using System.Text.Json;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class SiloLauncherProjectArgumentTests
{
    [Fact]
    public Task AutoStartServeWithDiagnosticsAsync_ProjectPathWithSpaces_PreservesProjectArgumentAndHostSeparator() =>
        SiloLauncherProcessHarness.RunAsync(typeof(SiloLauncherProjectArgumentTests), async root =>
        {
            await using var worker = new SiloLauncherFixtureWorker(root, earlyExit: false);
            using var shim = new SiloLauncherDotnetShim(root, worker.Executable);
            var projectDirectory = Directory.CreateDirectory(Path.Join(root, "project with spaces")).FullName;
            var project = Path.Join(projectDirectory, "custom-host.CSPROJ");
            await File.WriteAllTextAsync(project, "owned argv fixture; not an SDK project", TestContext.Current.CancellationToken);
            var launcher = SiloLauncherProcessHarness.Create(new CliConfig { SiloPath = project, DefaultPort = worker.Port });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            worker.ReleasePort();

            var launching = launcher.AutoStartServeWithDiagnosticsAsync(timeout.Token);
            await worker.ObserveStartedAsync(launching);
            var result = await launching;

            result.Success.ShouldBeTrue(result.Reason);
            result.Reason.ShouldBeNull();
            using var arguments = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Join(root, "worker.args"), timeout.Token));
            arguments.RootElement.EnumerateArray().Select(argument => argument.GetString()).ShouldBe(
                ["run", "--project", project, "--", "--Weave:LocalMode=true", $"--urls=http://localhost:{worker.Port}"]);
            (await File.ReadAllLinesAsync(Path.Join(root, "health-requests"), timeout.Token))
                .ShouldAllBe(request => request == "/health");
        });
}
