using System.Text.Json;

namespace Weave.Cli.Commands;

internal sealed class VersionCacheStore
{
    private static readonly string _weaveHome = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave");

    private static readonly string _cachePath = Path.Combine(_weaveHome, "update-cache.json");

    private readonly string _cacheFilePath;

    public VersionCacheStore()
        : this(_cachePath)
    {
    }

    internal VersionCacheStore(string cacheFilePath)
    {
        _cacheFilePath = cacheFilePath;
    }

    public UpdateCache? Load()
    {
        try
        {
            if (!File.Exists(_cacheFilePath))
                return null;

            var json = File.ReadAllText(_cacheFilePath);
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
            var directory = Path.GetDirectoryName(_cacheFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(cache, VersionJsonContext.Default.UpdateCache);
            File.WriteAllText(_cacheFilePath, json);
        }
        catch
        {
            // best-effort cache writes must not break CLI startup
        }
    }
}
