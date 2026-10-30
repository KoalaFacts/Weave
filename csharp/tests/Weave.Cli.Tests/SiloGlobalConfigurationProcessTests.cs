namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class SiloGlobalConfigurationProcessTests
{
    [Fact]
    public Task StartSilo_GlobalStorageAndAuth_PreservesArgumentBoundaries() =>
        SiloLauncherProcessHarness.RunAsync(typeof(SiloGlobalConfigurationProcessTests),
            root => SiloLifecycleConfiguration.VerifyAsync(root, "sqlserver", false));
}
