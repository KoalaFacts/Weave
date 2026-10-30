namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class SiloWorkspaceSqliteProcessTests
{
    [Fact]
    public Task StartSilo_WorkspaceSqlite_UsesSqliteConnectionKey() =>
        SiloLauncherProcessHarness.RunAsync(typeof(SiloWorkspaceSqliteProcessTests),
            root => SiloLifecycleConfiguration.VerifyAsync(root, "sqlite", true));
}
