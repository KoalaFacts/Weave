namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class SiloLauncherInvalidExecutableTests
{
    [Fact]
    public Task AutoStartServeWithDiagnosticsAsync_NonExecutableFile_ReturnsLaunchFailureWithLogLocation() =>
        SiloLauncherProcessHarness.RunAsync(typeof(SiloLauncherInvalidExecutableTests), async root =>
        {
            var executable = Path.Join(root, "not-executable");
            await File.WriteAllTextAsync(executable, "not an executable", TestContext.Current.CancellationToken);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var launcher = SiloLauncherProcessHarness.Create(new CliConfig { SiloPath = executable });

            var result = await launcher.AutoStartServeWithDiagnosticsAsync(TestContext.Current.CancellationToken);

            result.Success.ShouldBeFalse();
            result.Reason.ShouldNotBeNull();
            result.Reason.ShouldStartWith("Failed to launch the Host:");
            result.Reason.ShouldContain(executable);
            result.LogPath.ShouldBe(Path.Join(root, ".weave", "silo.log"));
            var log = await File.ReadAllTextAsync(result.LogPath, TestContext.Current.CancellationToken);
            log.ShouldContain($"cwd: {root}");
            log.ShouldContain("--Weave:LocalMode=true");
        });
}
