using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
[Collection(nameof(ShellConsoleGroup))]
public sealed class WorkspaceListDiskStatusTests
{
    [Fact]
    public Task List_ReportsPersistedReadyInvalidAndMissingPathsWithoutChangingRegistration() =>
        SiloLauncherProcessHarness.RunAsync(typeof(WorkspaceListDiskStatusTests), async root =>
        {
            var registry = new WorkspaceRegistry();
            using (var empty = new ShellOutputCapture())
            {
                var result = await new WorkspaceListCliCommand(registry)
                    .ExecuteAsync(new NoCliOptions(), TestContext.Current.CancellationToken);
                result.ShouldBe(0);
                empty.Text.ShouldContain("No workspaces found.");
                Directory.Exists(Path.Join(root, ".weave")).ShouldBeFalse();
            }

            var ready = Directory.CreateDirectory(Path.Join(root, "ready")).FullName;
            var invalid = Directory.CreateDirectory(Path.Join(root, "invalid")).FullName;
            var missing = Path.Join(root, "missing");
            const string manifest = """{"version":"1.0","name":"ready-entry"}""";
            await File.WriteAllTextAsync(Path.Join(ready, "workspace.json"), manifest, TestContext.Current.CancellationToken);
            registry.Register("ready-entry", ready);
            registry.Register("invalid-entry", invalid);
            registry.Register("missing-entry", missing);
            var registryPath = Path.Join(root, ".weave", "workspaces.json");
            var saved = await File.ReadAllTextAsync(registryPath, TestContext.Current.CancellationToken);
            var entries = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
            using var output = new ShellOutputCapture();

            var listed = await new WorkspaceListCliCommand(new WorkspaceRegistry())
                .ExecuteAsync(new NoCliOptions(), TestContext.Current.CancellationToken);

            listed.ShouldBe(0);
            foreach (var (name, path, state) in new[]
            {
                ("ready-entry", ready, "Ready"),
                ("invalid-entry", invalid, "Invalid"),
                ("missing-entry", missing, "Missing")
            })
            {
                var row = output.Text.Split('\n').Single(line => line.Contains(name, StringComparison.Ordinal));
                row.ShouldContain(path);
                row.ShouldContain(state);
            }
            output.Text.ShouldNotContain("No workspaces found.");
            (await File.ReadAllTextAsync(registryPath, TestContext.Current.CancellationToken)).ShouldBe(saved);
            (await File.ReadAllTextAsync(Path.Join(ready, "workspace.json"), TestContext.Current.CancellationToken)).ShouldBe(manifest);
            Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ShouldBe(entries);
        });
}
