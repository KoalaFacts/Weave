using System.Reflection;
using System.Text.Json;

namespace Weave.Cli.Shell;

internal sealed class VersionService(TimeProvider timeProvider)
{
    public const string UpgradeCommand = "dotnet tool update --global Weave.Cli";
    private const string NuGetIndexUrl = "https://api.nuget.org/v3-flatcontainer/weave.cli/index.json";

    private static readonly TimeSpan _cacheTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan _networkTimeout = TimeSpan.FromSeconds(3);
    private static readonly string _weaveHome = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave");

    private static readonly string _cachePath = Path.Combine(_weaveHome, "update-cache.json");

    public static string Current()
    {
        var assembly = typeof(VersionService).Assembly;
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    public static bool IsEnabled()
    {
        var value = Environment.GetEnvironmentVariable("WEAVE_NO_UPDATE_CHECK");
        return !(string.Equals(value, "1", StringComparison.Ordinal)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
    }

    public static UpdateCache? LoadCache() => LoadCache(_cachePath);

    internal static UpdateCache? LoadCache(string cacheFilePath)
    {
        try
        {
            if (!File.Exists(cacheFilePath))
                return null;

            var json = File.ReadAllText(cacheFilePath);
            return JsonSerializer.Deserialize(json, VersionJsonContext.Default.UpdateCache);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static string? PendingUpdateFromCache()
    {
        var cache = LoadCache();
        if (cache is null || string.IsNullOrWhiteSpace(cache.LatestVersion))
            return null;

        var current = Current();
        return IsNewer(cache.LatestVersion, current) ? cache.LatestVersion : null;
    }

    public void KickOffRefreshIfStale()
    {
        if (!IsEnabled())
            return;

        var cache = LoadCache();
        if (cache is not null && timeProvider.GetUtcNow() - cache.CheckedAt < _cacheTtl)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var client = CreateNuGetClient();
                var latest = await FetchLatestAsync(client, new Uri(NuGetIndexUrl), CancellationToken.None);
                if (latest is not null)
                    SaveCache(_cachePath, new UpdateCache { LatestVersion = latest, CheckedAt = timeProvider.GetUtcNow() });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or IOException)
            {
                System.Diagnostics.Trace.TraceWarning($"Weave update check failed: {ex.Message}");
            }
        });
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct)
    {
        using var client = CreateNuGetClient();
        return await CheckAsync(_cachePath, client, new Uri(NuGetIndexUrl), ct);
    }

    internal async Task<UpdateCheckResult> CheckAsync(
        string cacheFilePath,
        HttpClient client,
        Uri indexUri,
        CancellationToken ct)
    {
        var current = Current();
        var now = timeProvider.GetUtcNow();
        if (!IsEnabled())
        {
            return new UpdateCheckResult(
                current,
                null,
                now,
                false,
                "Update checks are disabled (WEAVE_NO_UPDATE_CHECK).");
        }

        try
        {
            var latest = await FetchLatestAsync(client, indexUri, ct);
            if (latest is null)
                return new UpdateCheckResult(current, null, now, false, "Could not reach NuGet.");

            SaveCache(cacheFilePath, new UpdateCache { LatestVersion = latest, CheckedAt = now });
            var newer = IsNewer(latest, current);
            return new UpdateCheckResult(current, latest, now, newer, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or IOException)
        {
            return new UpdateCheckResult(current, null, now, false, ex.Message);
        }
    }

    public static bool IsNewer(string candidate, string baseline) =>
        Parse(candidate).CompareTo(Parse(baseline)) > 0;

    internal static void SaveCache(string cacheFilePath, UpdateCache cache)
    {
        try
        {
            var directory = Path.GetDirectoryName(cacheFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(cache, VersionJsonContext.Default.UpdateCache);
            File.WriteAllText(cacheFilePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best-effort cache writes must not break CLI startup
        }
    }

    internal static async Task<string?> FetchLatestAsync(HttpClient client, Uri indexUri, CancellationToken ct)
    {
        using var response = await client.GetAsync(indexUri, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!document.RootElement.TryGetProperty("versions", out var versions)
            || versions.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? latest = null;
        foreach (var element in versions.EnumerateArray())
        {
            var version = element.GetString();
            if (string.IsNullOrWhiteSpace(version))
                continue;
            if (version.Contains('-', StringComparison.Ordinal))
                continue;

            if (latest is null || IsNewer(version, latest))
                latest = version;
        }

        return latest;
    }

    private static HttpClient CreateNuGetClient() => new() { Timeout = _networkTimeout };

    private static Version Parse(string version)
    {
        var dash = version.IndexOf('-', StringComparison.Ordinal);
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        var cut = -1;
        if (dash >= 0)
            cut = dash;
        if (plus >= 0 && (cut == -1 || plus < cut))
            cut = plus;

        var numeric = cut >= 0 ? version[..cut] : version;
        return Version.TryParse(numeric, out var parsed) ? parsed : new Version(0, 0, 0);
    }
}
