namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class SiloWorkspaceRedisProcessTests
{
    [Fact]
    public Task StartSilo_WorkspaceRedis_UsesRedisConnectionKey() =>
        SiloLauncherProcessHarness.RunAsync(typeof(SiloWorkspaceRedisProcessTests),
            root => SiloLifecycleConfiguration.VerifyAsync(root, "redis", true));
}
