namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class ManifestResolverAncestorBoundaryTests
{
    [Fact]
    public Task Resolve_NestedChildDirectory_SelectsNearestManifestWithoutChangingNamedResolution() =>
        SiloLauncherProcessHarness.RunAsync(typeof(ManifestResolverAncestorBoundaryTests), async root =>
        {
            var rootManifest = Path.Join(root, "workspace.json");
            var near = Directory.CreateDirectory(Path.Join(root, "nested")).FullName;
            var deep = Directory.CreateDirectory(Path.Join(near, "source", "feature")).FullName;
            var nearManifest = Path.Join(near, "workspace.json");
            var localManifest = Path.Join(deep, "workspace.json");
            await File.WriteAllTextAsync(rootManifest, "{}", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(nearManifest, "{}", TestContext.Current.CancellationToken);
            var registry = new WorkspaceRegistry();
            registry.Register("root-workspace", root);
            var resolver = new ManifestResolver(registry);
            var previous = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = deep;
                resolver.Resolve(null).ShouldBe(nearManifest);
                resolver.Resolve("root-workspace").ShouldBe(rootManifest);
                resolver.Resolve("unknown-workspace").ShouldBeNull();

                await File.WriteAllTextAsync(localManifest, "{}", TestContext.Current.CancellationToken);
                resolver.Resolve(null).ShouldBe(localManifest);
                File.Delete(localManifest);
                File.Delete(nearManifest);
                resolver.Resolve(null).ShouldBe(rootManifest);
            }
            finally
            {
                Environment.CurrentDirectory = previous;
            }
        });
}
