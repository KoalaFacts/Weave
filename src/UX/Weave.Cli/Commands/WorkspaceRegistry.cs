using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weave.Cli.Commands;

/// <summary>
/// Manages a mapping of workspace names to their folder paths.
/// Stored at ~/.weave/workspaces.json so workspaces can live anywhere on disk.
/// </summary>
internal static class WorkspaceRegistry
{
    private static readonly string WeaveHome = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave");

    private static readonly string RegistryPath = Path.Combine(WeaveHome, "workspaces.json");

    public static void Register(string name, string absolutePath)
    {
        var entries = Load();
        entries[name] = absolutePath;
        Save(entries);
    }

    public static void Unregister(string name)
    {
        var entries = Load();
        entries.Remove(name);
        Save(entries);
    }

    public static string? Resolve(string name)
    {
        var entries = Load();
        return entries.TryGetValue(name, out var path) ? path : null;
    }

    public static IReadOnlyDictionary<string, string> GetAll() => Load();

    public static IEnumerable<string> GetNames() => Load().Keys;

    private static Dictionary<string, string> Load()
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

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class WorkspaceRegistryJsonContext : JsonSerializerContext;
