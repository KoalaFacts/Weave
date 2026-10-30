using Weave.Cli.Shell;

namespace Weave.Cli.Tests;

public sealed class ManifestResolverTests
{
    [Fact]
    public void Resolve_NameInRegistry_ReturnsManifestPathWhenFileExists()
    {
        var dir = Directory.CreateTempSubdirectory("weave-manifest-resolver-test-");
        try
        {
            var manifestPath = Path.Combine(dir.FullName, "workspace.json");
            File.WriteAllText(manifestPath, "{}");

            var registry = new StubRegistry { ["my-workspace"] = dir.FullName };
            var resolver = new ManifestResolver(registry);

            resolver.Resolve("my-workspace").ShouldBe(manifestPath);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Resolve_NameInRegistryButManifestMissing_ReturnsNull()
    {
        var dir = Directory.CreateTempSubdirectory("weave-manifest-resolver-test-");
        try
        {
            var registry = new StubRegistry { ["my-workspace"] = dir.FullName };
            var resolver = new ManifestResolver(registry);

            resolver.Resolve("my-workspace").ShouldBeNull();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Resolve_UnknownName_ReturnsNull()
    {
        var resolver = new ManifestResolver(new StubRegistry());

        resolver.Resolve("nonexistent").ShouldBeNull();
    }

    private sealed class StubRegistry : IWorkspaceRegistry
    {
        private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);

        public string this[string name] { set => _entries[name] = value; }

        public void Register(string name, string absolutePath) => _entries[name] = absolutePath;
        public void Unregister(string name) => _entries.Remove(name);
        public string? Resolve(string name) => _entries.TryGetValue(name, out var path) ? path : null;
        public IReadOnlyDictionary<string, string> GetAll() => _entries;
        public IEnumerable<string> GetNames() => _entries.Keys;
    }
}
