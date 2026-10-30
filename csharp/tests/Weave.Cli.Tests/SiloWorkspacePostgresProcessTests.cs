namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class SiloWorkspacePostgresProcessTests
{
    [Fact]
    public Task StartSilo_WorkspacePostgres_OverridesGlobalConnection() =>
        SiloLauncherProcessHarness.RunAsync(typeof(SiloWorkspacePostgresProcessTests),
            root => SiloLifecycleConfiguration.VerifyAsync(root, "postgresql", true));
}
