using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class WorkspacePluginRejectionProcessTests
{
    [Fact]
    public Task AddPlugin_DuplicateMissingWorkspaceAndCancelledRead_LeaveManifestBytesAndAuthorityUnchanged() =>
        SiloLauncherProcessHarness.RunAsync(typeof(WorkspacePluginRejectionProcessTests), async root =>
        {
            var fixture = new WorkspacePluginDiskFixture(root);
            var original = await File.ReadAllBytesAsync(fixture.ManifestPath, TestContext.Current.CancellationToken);
            using (var console = new MarketplacePromptConsole())
            {
                (await fixture.Command.ExecuteAsync(new WorkspaceAddPluginOptions(WorkspacePluginDiskFixture.Name, "retained", "custom"),
                    TestContext.Current.CancellationToken)).ShouldBe(1);
                console.Text.ShouldContain("Plugin 'retained' already exists in the workspace.");
                console.Text.ShouldNotContain("Description (optional):");
                console.RemainingKeys.ShouldBe(0);
                (await File.ReadAllBytesAsync(fixture.ManifestPath, TestContext.Current.CancellationToken)).ShouldBe(original);
            }
            using (var console = new MarketplacePromptConsole())
            {
                (await fixture.Command.ExecuteAsync(new WorkspaceAddPluginOptions("missing-plugin-workspace", null, null),
                    TestContext.Current.CancellationToken)).ShouldBe(1);
                console.Text.ShouldContain("No workspace.json found for 'missing-plugin-workspace'.");
                console.Text.ShouldNotContain("Select plugin type:");
                console.RemainingKeys.ShouldBe(0);
                (await File.ReadAllBytesAsync(fixture.ManifestPath, TestContext.Current.CancellationToken)).ShouldBe(original);
            }
            using (var console = new MarketplacePromptConsole())
            using (var cancelled = new CancellationTokenSource())
            {
                await cancelled.CancelAsync();
                await Should.ThrowAsync<OperationCanceledException>(() => fixture.Command.ExecuteAsync(
                    new WorkspaceAddPluginOptions(WorkspacePluginDiskFixture.Name, "never-added", "custom"), cancelled.Token));
                console.Text.ShouldNotContain("added to workspace");
                console.RemainingKeys.ShouldBe(0);
                (await File.ReadAllBytesAsync(fixture.ManifestPath, TestContext.Current.CancellationToken)).ShouldBe(original);
            }
            fixture.AssertUnrelatedStatePreserved(await fixture.ReadAsync());
        });
}
