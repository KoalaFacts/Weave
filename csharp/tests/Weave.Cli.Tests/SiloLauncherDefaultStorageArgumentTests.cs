using System.Text.Json;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class SiloLauncherDefaultStorageArgumentTests
{
    [Fact]
    public Task AutoStartServeWithDiagnosticsAsync_SqliteWithoutConnectionOverride_PassesStorageWithoutInventingConnectionString() =>
        SiloLauncherProcessHarness.RunAsync(typeof(SiloLauncherDefaultStorageArgumentTests), async root =>
        {
            await using var worker = new SiloLauncherFixtureWorker(root, earlyExit: false);
            var config = new CliConfig { SiloPath = worker.Executable, DefaultPort = worker.Port, Storage = "sqlite" };
            var launcher = new SiloLauncher(new ReadOnlyConfig(config), new CliSecretResolver());
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
                ["--Weave:LocalMode=true", $"--urls=http://localhost:{worker.Port}", "--Weave:Storage=sqlite"]);
        });

    private sealed class ReadOnlyConfig(CliConfig config) : IConfigStore
    {
        public CliConfig Load() => config;
        public bool Exists() => true;
        public void Save(CliConfig value) => throw new InvalidOperationException("Launching must not write CLI configuration.");
    }
}
