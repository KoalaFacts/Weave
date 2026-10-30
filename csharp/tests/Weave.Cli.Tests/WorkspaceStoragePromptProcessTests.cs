using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class WorkspaceStoragePromptProcessTests
{
    [Fact]
    public Task Change_BackendAndConnectionDefaultsThenMemoryReset_OnlyUpdatesSelectedManifest() =>
        SiloLauncherProcessHarness.RunAsync(typeof(WorkspaceStoragePromptProcessTests), async root =>
        {
            var fixture = new WorkspaceStorageDiskFixture(root);
            using (var console = new MarketplacePromptConsole())
            {
                console.Key(ConsoleKey.DownArrow);
                console.Line("");
                console.Line("");
                (await fixture.Change.ExecuteAsync(new WorkspaceStorageChangeOptions(
                    WorkspaceStorageDiskFixture.Name, null, null, null, null, null), TestContext.Current.CancellationToken)).ShouldBe(0);
                console.RemainingKeys.ShouldBe(0);
                console.Text.ShouldContain("Backend:");
                console.Text.ShouldContain("Connection string:");
                var storage = (await fixture.ReadAsync()).Workspace.Storage.ShouldNotBeNull();
                storage.Backend.ShouldBe("sqlite");
                storage.Database.ShouldBe("weave");
                storage.ConnectionString.ShouldBe($"Data Source={fixture.DatabasePath}");
                storage.Schema.ShouldBeNull();
                File.Exists(fixture.DatabasePath).ShouldBeFalse();
            }
            using (var console = new MarketplacePromptConsole())
            {
                (await fixture.Change.ExecuteAsync(new WorkspaceStorageChangeOptions(
                    WorkspaceStorageDiskFixture.Name, "memory", null, null, null, null), TestContext.Current.CancellationToken)).ShouldBe(0);
                (await fixture.ReadAsync()).Workspace.Storage.ShouldBeNull();
                console.Text.ShouldContain("reset to global default storage.");
                console.Text.ShouldNotContain("Connection string:");
                console.RemainingKeys.ShouldBe(0);
            }
            fixture.AssertOtherStatePreserved();
        });
}
