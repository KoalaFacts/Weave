using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
[Collection(nameof(ShellConsoleGroup))]
public sealed class WorkspaceRegisteredSelectionProcessTests
{
    [Fact]
    public Task Show_ChoosingSecondRegisteredWorkspace_ReadsItsManifestWithoutChangingDisk() =>
        SiloLauncherProcessHarness.RunAsync(typeof(WorkspaceRegisteredSelectionProcessTests), async root =>
        {
            var registry = new WorkspaceRegistry();
            var first = Directory.CreateDirectory(Path.Join(root, "first")).FullName;
            var second = Directory.CreateDirectory(Path.Join(root, "second")).FullName;
            const string firstJson = """{"name":"unselected-workspace-marker","version":"1.0"}""";
            const string secondJson = """{"name":"selected-workspace-marker","version":"1.0"}""";
            var firstPath = Path.Join(first, "workspace.json");
            var secondPath = Path.Join(second, "workspace.json");
            await File.WriteAllTextAsync(firstPath, firstJson, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(secondPath, secondJson, TestContext.Current.CancellationToken);
            registry.Register("first", first);
            registry.Register("second", second);
            var registryPath = Path.Join(root, ".weave", "workspaces.json");
            var registryJson = await File.ReadAllTextAsync(registryPath, TestContext.Current.CancellationToken);
            var before = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
            var resolver = new ManifestResolver(registry);
            resolver.Resolve(null).ShouldBeNull("The menu must be reached rather than selecting a current-directory manifest.");
            using var console = new ScriptedSelectionConsole(ConsoleKey.DownArrow, ConsoleKey.Enter);
            var command = new WorkspaceShowCliCommand(resolver, new WorkspacePrompt(registry, resolver));

            var result = await command.ExecuteAsync(new WorkspaceNameOptions(null), TestContext.Current.CancellationToken);

            result.ShouldBe(0);
            console.RemainingKeys.ShouldBe(0);
            console.Output.ShouldContain("Which workspace would you like to show?");
            console.Output.ShouldContain("selected-workspace-marker");
            console.Output.ShouldNotContain("unselected-workspace-marker");
            (await File.ReadAllTextAsync(firstPath, TestContext.Current.CancellationToken)).ShouldBe(firstJson);
            (await File.ReadAllTextAsync(secondPath, TestContext.Current.CancellationToken)).ShouldBe(secondJson);
            (await File.ReadAllTextAsync(registryPath, TestContext.Current.CancellationToken)).ShouldBe(registryJson);
            Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ShouldBe(before);
        });
}
