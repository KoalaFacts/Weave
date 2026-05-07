using System.Text.Json;

namespace Weave.Cli.Shell;

/// <summary>
/// File-backed mapping of workspace names to folder paths at
/// <c>~/.weave/workspaces.json</c>, so workspaces can live anywhere on disk.
/// </summary>
internal sealed class WorkspaceRegistry : IWorkspaceRegistry
{
    private static readonly string WeaveHome = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave");

    private static readonly string RegistryPath = Path.Combine(WeaveHome, "workspaces.json");

    public void Register(string name, string absolutePath)
    {
        var entries = LoadEntries();
        entries[name] = absolutePath;
        Save(entries);
    }

    public void Unregister(string name)
    {
        var entries = LoadEntries();
        entries.Remove(name);
        Save(entries);
    }

    public string? Resolve(string name)
    {
        var entries = LoadEntries();
        return entries.TryGetValue(name, out var path) ? path : null;
    }

    public IReadOnlyDictionary<string, string> GetAll() => LoadEntries();

    public IEnumerable<string> GetNames() => LoadEntries().Keys;

    private static Dictionary<string, string> LoadEntries()
    {
        if (!File.Exists(RegistryPath))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var json = File.ReadAllText(RegistryPath);
        return JsonSerializer.Deserialize(json, WorkspaceRegistryJsonContext.Default.DictionaryStringString)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private static void Save(Dictionary<string, string> entries)
    {
        Directory.CreateDirectory(WeaveHome);
        var json = JsonSerializer.Serialize(entries, WorkspaceRegistryJsonContext.Default.DictionaryStringString);
        File.WriteAllText(RegistryPath, json);
    }
}
