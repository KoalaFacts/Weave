using Weave.Cli.Commands;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Tests;

internal sealed class WorkspacePluginDiskFixture
{
    public const string Name = "plugin-team";
    public string ManifestPath { get; }
    public WorkspaceAddPluginCliCommand Command { get; }
    private readonly string _registryPath;
    private readonly string _registry;
    private readonly string _siblingPath;
    private readonly string _globalPath;
    private readonly string _original;

    public WorkspacePluginDiskFixture(string root)
    {
        var folder = Directory.CreateDirectory(Path.Join(root, "selected workspace")).FullName;
        ManifestPath = Path.Join(folder, "workspace.json");
        _original = """
            {"name":"plugin-team","version":"1.0","workspace":{"isolation":"full"},
             "agents":{"assistant":{"model":"retained-model","tools":["documents"],"capabilities":["tool:documents:invoke:read_file"]}},
             "tools":{"documents":{"type":"filesystem"}},
             "plugins":{"retained":{"type":"custom","description":"retained-description","config":{"marker":"retained-value"},
               "requires":{"dependency":"retained-requirement"},"enabledWhen":"retained-flag"}}}
            """;
        File.WriteAllText(ManifestPath, _original);
        var sibling = Directory.CreateDirectory(Path.Join(root, "sibling workspace")).FullName;
        _siblingPath = Path.Join(sibling, "workspace.json");
        File.WriteAllText(_siblingPath, _original);
        var registry = new WorkspaceRegistry();
        registry.Register(Name, folder);
        registry.Register("sibling", sibling);
        _registryPath = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave", "workspaces.json");
        _registry = File.ReadAllText(_registryPath);
        _globalPath = Path.Join(Path.GetDirectoryName(_registryPath)!, "config.json");
        File.WriteAllText(_globalPath, "global configuration sentinel");
        var resolver = new ManifestResolver(registry);
        Command = new WorkspaceAddPluginCliCommand(resolver, new WorkspacePrompt(registry, resolver));
    }

    public Task<WorkspaceManifest> ReadAsync() => WorkspaceManifestFile.ReadAsync(ManifestPath, TestContext.Current.CancellationToken);

    public void AssertUnrelatedStatePreserved(WorkspaceManifest actual)
    {
        var parser = new ManifestParser();
        var before = parser.Parse(_original);
        parser.Validate(actual).ShouldBeEmpty();
        parser.Serialize(actual with { Plugins = new Dictionary<string, PluginDefinition> { ["retained"] = actual.Plugins["retained"] } })
            .ShouldBe(parser.Serialize(before));
        File.ReadAllText(_registryPath).ShouldBe(_registry);
        File.ReadAllText(_siblingPath).ShouldBe(_original);
        File.ReadAllText(_globalPath).ShouldBe("global configuration sentinel");
    }
}
