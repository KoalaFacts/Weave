using System.Text.Json;

namespace Weave.Cli.Commands;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is kept testable and replaceable from version services.")]
internal sealed class VersionCacheStore
{
    private static readonly string _weaveHome = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave");

    private static readonly string _cachePath = Path.Combine(_weaveHome, "update-cache.json");

    public UpdateCache? Load()
    {
        try
        {
            if (!File.Exists(_cachePath))
                return null;

            var json = File.ReadAllText(_cachePath);
            return JsonSerializer.Deserialize(json, VersionJsonContext.Default.UpdateCache);
        }
        catch
        {
            return null;
        }
    }

    public void Save(UpdateCache cache)
    {
        try
        {
            Directory.CreateDirectory(_weaveHome);
            var json = JsonSerializer.Serialize(cache, VersionJsonContext.Default.UpdateCache);
            File.WriteAllText(_cachePath, json);
        }
        catch
        {
            // best-effort cache writes must not break CLI startup
        }
    }
}
