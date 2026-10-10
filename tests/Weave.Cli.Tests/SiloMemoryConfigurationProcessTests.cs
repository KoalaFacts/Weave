namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class SiloMemoryConfigurationProcessTests
{
    [Fact]
    public Task StartSilo_Memory_LeavesStorageArgumentsAbsentAndPreservesAuth() =>
        SiloLauncherProcessHarness.RunAsync(typeof(SiloMemoryConfigurationProcessTests),
            root => SiloLifecycleConfiguration.VerifyAsync(root, "memory", false));
}
