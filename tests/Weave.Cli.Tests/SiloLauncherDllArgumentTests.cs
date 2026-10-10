using System.Text.Json;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class SiloLauncherDllArgumentTests
{
    [Fact]
    public Task AutoStartServeWithDiagnosticsAsync_DllPathWithSpaces_PassesAssemblyDirectlyToOwnedDotnet() =>
        SiloLauncherProcessHarness.RunAsync(typeof(SiloLauncherDllArgumentTests), async root =>
        {
            await using var worker = new SiloLauncherFixtureWorker(root, earlyExit: false);
            using var shim = new SiloLauncherDotnetShim(root, worker.Executable);
            var assembly = Path.Join(root, "published host.DLL");
            await File.WriteAllTextAsync(assembly, "owned argv fixture; not a managed assembly", TestContext.Current.CancellationToken);
            var launcher = SiloLauncherProcessHarness.Create(new CliConfig { SiloPath = assembly, DefaultPort = worker.Port });
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
                [assembly, "--Weave:LocalMode=true", $"--urls=http://localhost:{worker.Port}"]);
        });
}
