using System.Text.Json;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class WorkspaceRegistryPersistenceBoundaryTests
{
    [Fact]
    public Task RegisterAndUnregister_IsolatedProfile_PreservesUnrelatedEntriesAcrossInstances() =>
        SiloLauncherProcessHarness.RunAsync(typeof(WorkspaceRegistryPersistenceBoundaryTests), root =>
        {
            var home = Path.Join(root, ".weave");
            var registryPath = Path.Join(home, "workspaces.json");
            Directory.Exists(home).ShouldBeFalse();
            var registry = new WorkspaceRegistry();
            registry.GetAll().ShouldBeEmpty();
            registry.Resolve("reviews").ShouldBeNull();
            var reviews = Directory.CreateDirectory(Path.Join(root, "reviews")).FullName;
            var docs = Directory.CreateDirectory(Path.Join(root, "docs")).FullName;
            var relocated = Directory.CreateDirectory(Path.Join(root, "reviews-relocated")).FullName;

            registry.Register("reviews", reviews);
            registry.Register("docs", docs);
            new WorkspaceRegistry().Register("reviews", relocated);
            var loaded = new WorkspaceRegistry();

            loaded.Resolve("reviews").ShouldBe(relocated);
            loaded.Resolve("docs").ShouldBe(docs);
            loaded.GetNames().Order(StringComparer.Ordinal).ShouldBe(["docs", "reviews"]);
            using (var persisted = JsonDocument.Parse(File.ReadAllText(registryPath)))
            {
                persisted.RootElement.GetProperty("reviews").GetString().ShouldBe(relocated);
                persisted.RootElement.GetProperty("docs").GetString().ShouldBe(docs);
            }

            loaded.Unregister("reviews");
            var remaining = new WorkspaceRegistry().GetAll();
            remaining.Keys.ShouldBe(["docs"]);
            remaining["docs"].ShouldBe(docs);
            using var afterRemoval = JsonDocument.Parse(File.ReadAllText(registryPath));
            afterRemoval.RootElement.TryGetProperty("reviews", out _).ShouldBeFalse();
            afterRemoval.RootElement.GetProperty("docs").GetString().ShouldBe(docs);
            return Task.CompletedTask;
        });
}
