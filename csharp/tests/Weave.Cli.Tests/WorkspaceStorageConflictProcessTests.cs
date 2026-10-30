using Weave.Cli.Commands;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class WorkspaceStorageConflictProcessTests
{
    [Fact]
    public Task Change_ExistingSqlite_StopOverrideAndRenamePreserveExistingData() =>
        SiloLauncherProcessHarness.RunAsync(typeof(WorkspaceStorageConflictProcessTests), async root =>
        {
            for (var selection = 0; selection < 3; selection++)
            {
                var fixture = new WorkspaceStorageDiskFixture(Path.Join(root, selection.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                await File.WriteAllTextAsync(fixture.DatabasePath, "retained database sentinel", TestContext.Current.CancellationToken);
                var before = await File.ReadAllBytesAsync(fixture.ManifestPath, TestContext.Current.CancellationToken);
                var renamed = Path.Join(fixture.Folder, ".weave", "renamed.db");
                using var console = new MarketplacePromptConsole();
                for (var index = 0; index < selection; index++)
                    console.Key(ConsoleKey.DownArrow);
                console.Line("");
                if (selection == 2)
                    console.Line(renamed);

                var result = await fixture.Change.ExecuteAsync(new WorkspaceStorageChangeOptions(
                    WorkspaceStorageDiskFixture.Name, "sqlite", $"Data Source={fixture.DatabasePath}", null, "original", null),
                    TestContext.Current.CancellationToken);

                result.ShouldBe(0);
                console.RemainingKeys.ShouldBe(0);
                console.Text.ShouldContain("Database 'original' already exists.");
                if (selection == 0)
                {
                    console.Text.ShouldContain("Aborted. No changes made.");
                    (await File.ReadAllBytesAsync(fixture.ManifestPath, TestContext.Current.CancellationToken)).ShouldBe(before);
                }
                else
                {
                    var manifest = await fixture.ReadAsync();
                    var storage = manifest.Workspace.Storage.ShouldNotBeNull();
                    storage.Backend.ShouldBe("sqlite");
                    storage.Isolation.ShouldBe(StorageIsolation.Database);
                    storage.Database.ShouldBe(selection == 2 ? renamed : "original");
                    storage.ConnectionString.ShouldBe($"Data Source={(selection == 2 ? renamed : fixture.DatabasePath)}");
                    manifest.Agents["assistant"].Model.ShouldBe("fixture-model");
                    console.Text.ShouldContain("storage set to sqlite.");
                }
                File.Exists(renamed).ShouldBeFalse();
                File.ReadAllText(fixture.DatabasePath).ShouldBe("retained database sentinel");
                fixture.AssertOtherStatePreserved();
            }
        });
}
