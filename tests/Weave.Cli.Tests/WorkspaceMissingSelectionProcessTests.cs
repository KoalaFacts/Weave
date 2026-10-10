using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
[Collection(nameof(ShellConsoleGroup))]
public sealed class WorkspaceMissingSelectionProcessTests
{
    [Fact]
    public Task Show_WithoutManifestOrRegistration_ReportsFailureWithoutPromptOrDiskWrites() =>
        SiloLauncherProcessHarness.RunAsync(typeof(WorkspaceMissingSelectionProcessTests), async root =>
        {
            var registry = new WorkspaceRegistry();
            var resolver = new ManifestResolver(registry);
            resolver.Resolve(null).ShouldBeNull();
            registry.GetAll().ShouldBeEmpty();
            var before = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
            using var console = new ScriptedSelectionConsole();
            var command = new WorkspaceShowCliCommand(resolver, new WorkspacePrompt(registry, resolver));

            var result = await command.ExecuteAsync(new WorkspaceNameOptions(null), TestContext.Current.CancellationToken);

            result.ShouldBe(1);
            console.Output.ShouldContain("No workspace.json found. Create one first with: weave workspace new");
            console.Output.ShouldNotContain("Which workspace would you like to show?");
            Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ShouldBe(before);
        });
}
