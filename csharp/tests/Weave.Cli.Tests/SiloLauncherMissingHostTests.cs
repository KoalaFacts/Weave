namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class SiloLauncherMissingHostTests
{
    [Fact]
    public Task AutoStartServeWithDiagnosticsAsync_NoDiscoverableHost_ReturnsActionableFailureWithoutLogFile() =>
        SiloLauncherProcessHarness.RunAsync(typeof(SiloLauncherMissingHostTests), async root =>
        {
            File.Exists(Path.Join(AppContext.BaseDirectory, "host", "Weave.Silo")).ShouldBeFalse();
            File.Exists(Path.Join(AppContext.BaseDirectory, "Weave.Silo.dll")).ShouldBeFalse();
            var launcher = SiloLauncherProcessHarness.Create(new CliConfig { SiloPath = Path.Join(root, "missing-host") });

            var result = await launcher.AutoStartServeWithDiagnosticsAsync(TestContext.Current.CancellationToken);

            result.Success.ShouldBeFalse();
            result.Reason.ShouldBe("Could not locate the Weave Silo on disk.");
            result.LogPath.ShouldBe(Path.Join(root, ".weave", "silo.log"));
            File.Exists(result.LogPath).ShouldBeFalse();
            (await launcher.AutoStartServeAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();
        });
}
