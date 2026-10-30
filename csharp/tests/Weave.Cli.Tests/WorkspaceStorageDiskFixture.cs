using Weave.Cli.Commands;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Tests;

internal sealed class WorkspaceStorageDiskFixture
{
    public const string Name = "storage-team";
    public string Folder { get; }
    public string ManifestPath => Path.Join(Folder, "workspace.json");
    public string DatabasePath => Path.Join(Folder, ".weave", "workspace.db");
    public WorkspaceStorageChangeCliCommand Change { get; }
    private readonly string _registryPath;
    private readonly string _registry;
    private readonly string _globalPath;
    private readonly string _sibling;
    private readonly string _siblingOriginal;

    public WorkspaceStorageDiskFixture(string root)
    {
        Folder = Directory.CreateDirectory(Path.Join(root, "workspace with spaces")).FullName;
        Directory.CreateDirectory(Path.Join(Folder, ".weave"));
        File.WriteAllText(ManifestPath, """
            {"name":"storage-team","version":"1.0","workspace":{"isolation":"full"},
             "agents":{"assistant":{"model":"fixture-model","tools":[],"capabilities":[]}},"tools":{}}
            """);
        var siblingFolder = Directory.CreateDirectory(Path.Join(root, "sibling workspace")).FullName;
        _sibling = Path.Join(siblingFolder, "workspace.json");
        _siblingOriginal = File.ReadAllText(ManifestPath).Replace("storage-team", "other-team", StringComparison.Ordinal);
        File.WriteAllText(_sibling, _siblingOriginal);
        var registry = new WorkspaceRegistry();
        registry.Register(Name, Folder);
        registry.Register("other-team", siblingFolder);
        _registryPath = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave", "workspaces.json");
        _registry = File.ReadAllText(_registryPath);
        _globalPath = Path.Join(Path.GetDirectoryName(_registryPath)!, "config.json");
        File.WriteAllText(_globalPath, "global configuration sentinel; workspace storage must not read or write it");
        var resolver = new ManifestResolver(registry);
        Change = new WorkspaceStorageChangeCliCommand(resolver, new WorkspacePrompt(registry, resolver));
    }

    public Task<WorkspaceManifest> ReadAsync() => WorkspaceManifestFile.ReadAsync(ManifestPath, TestContext.Current.CancellationToken);

    public void AssertOtherStatePreserved()
    {
        File.ReadAllText(_registryPath).ShouldBe(_registry);
        File.ReadAllText(_globalPath).ShouldBe("global configuration sentinel; workspace storage must not read or write it");
        File.ReadAllText(_sibling).ShouldBe(_siblingOriginal);
    }
}
