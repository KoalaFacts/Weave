using System.Text.Json;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class WorkspaceRegistryCorruptFileTests
{
    [Fact]
    public Task Register_CorruptExistingRegistry_ThrowsWithoutOverwritingStoredData() =>
        SiloLauncherProcessHarness.RunAsync(typeof(WorkspaceRegistryCorruptFileTests), async root =>
        {
            var home = Directory.CreateDirectory(Path.Join(root, ".weave")).FullName;
            var registryPath = Path.Join(home, "workspaces.json");
            const string original = "{\"reviews\":\"incomplete-preserved-marker\"";
            await File.WriteAllTextAsync(registryPath, original, TestContext.Current.CancellationToken);
            var registry = new WorkspaceRegistry();

            Should.Throw<JsonException>(() => registry.Register("new-workspace", Path.Join(root, "new-workspace")));

            (await File.ReadAllTextAsync(registryPath, TestContext.Current.CancellationToken)).ShouldBe(original);
        });
}
