using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
[Collection(nameof(ShellConsoleGroup))]
public sealed class MarketplaceInstallExistingDirectoryTests
{
    [Fact]
    public Task NonemptyWorkspaceDirectory_RejectsScaffoldingWithoutOverwritingOrRegistering() =>
        SiloLauncherProcessHarness.RunAsync(typeof(MarketplaceInstallExistingDirectoryTests), async root =>
        {
            using var output = new ShellOutputCapture();
            using var fixture = new MarketplaceInstallFlowFixture(new WorkspaceRegistry());
            var directory = Directory.CreateDirectory(Path.Join(root, "existing-workspace")).FullName;
            var manifestPath = Path.Join(directory, "workspace.json");
            const string existingManifest = "{\"name\":\"keep-existing-workspace\"}";
            File.WriteAllText(manifestPath, existingManifest);

            var result = await fixture.Command.ExecuteAsync(
                new MarketplaceInstallOptions("item?owned", WorkspaceName: "existing-workspace"),
                TestContext.Current.CancellationToken);

            result.ShouldBe(1);
            fixture.AssertInstallRequests();
            File.ReadAllText(manifestPath).ShouldBe(existingManifest);
            Directory.GetFileSystemEntries(directory).ShouldBe([manifestPath]);
            new WorkspaceRegistry().GetAll().ShouldBeEmpty();
            Directory.Exists(Path.Join(root, ".weave")).ShouldBeFalse();
            output.Text.ShouldContain("already exists and is not empty.");
            output.Text.ShouldNotContain("scaffolded");
        });
}
