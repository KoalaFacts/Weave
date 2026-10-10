namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class SiloLauncherPathResolutionTests
{
    [Fact]
    public Task ResolveSiloPath_EnvironmentThenConfigurationThenRepository_UsesExistingPathsInPriorityOrder() =>
        SiloLauncherProcessHarness.RunAsync(typeof(SiloLauncherPathResolutionTests), async root =>
        {
            var configured = Path.Join(root, "configured-host");
            var overridePath = Path.Join(root, "override-host");
            await File.WriteAllTextAsync(configured, "configured fixture", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(overridePath, "override fixture", TestContext.Current.CancellationToken);
            var launcher = SiloLauncherProcessHarness.Create(new CliConfig { SiloPath = configured });
            var previous = Environment.GetEnvironmentVariable("WEAVE_SILO_PATH");
            try
            {
                Environment.SetEnvironmentVariable("WEAVE_SILO_PATH", overridePath);
                launcher.ResolveSiloPath().ShouldBe(overridePath);
                Environment.SetEnvironmentVariable("WEAVE_SILO_PATH", Path.Join(root, "missing-override"));
                launcher.ResolveSiloPath().ShouldBe(configured);
                Environment.SetEnvironmentVariable("WEAVE_SILO_PATH", null);
                File.Delete(configured);
                File.Exists(Path.Join(AppContext.BaseDirectory, "host", "Weave.Silo")).ShouldBeFalse();
                var sourceDirectory = Directory.CreateDirectory(Path.Join(root, "hosts", "Weave.Host")).FullName;
                launcher.ResolveSiloPath().ShouldBe(sourceDirectory);
            }
            finally
            {
                Environment.SetEnvironmentVariable("WEAVE_SILO_PATH", previous);
            }
        });
}
