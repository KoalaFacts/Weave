using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
[Collection(nameof(ShellConsoleGroup))]
public sealed class MarketplaceInstallNoScaffoldDiskTests
{
    [Fact]
    public Task NoScaffold_InstallsItemWithoutCreatingWorkspaceOrRegistry() =>
        SiloLauncherProcessHarness.RunAsync(typeof(MarketplaceInstallNoScaffoldDiskTests), async root =>
        {
            using var output = new ShellOutputCapture();
            using var fixture = new MarketplaceInstallFlowFixture(new WorkspaceRegistry());
            var before = Directory.GetFileSystemEntries(root).Order(StringComparer.Ordinal).ToArray();

            var result = await fixture.Command.ExecuteAsync(
                new MarketplaceInstallOptions("item?owned", NoScaffold: true, WorkspaceName: "ignored-name"),
                TestContext.Current.CancellationToken);

            result.ShouldBe(0);
            fixture.AssertInstallRequests();
            Directory.GetFileSystemEntries(root).Order(StringComparer.Ordinal).ShouldBe(before);
            new WorkspaceRegistry().GetAll().ShouldBeEmpty();
            Directory.Exists(Path.Join(root, ".weave")).ShouldBeFalse();
            output.Text.ShouldContain("--workspace-name is ignored when --no-scaffold is set.");
            output.Text.ShouldContain("Installed: Review bundle [literal]");
            output.Text.ShouldNotContain("scaffolded");
        });
}
