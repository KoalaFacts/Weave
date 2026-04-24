using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weave.Cli.Commands;

/// <summary>
/// Installed-version detection and background update-check against NuGet.
///
/// Cache lives at <c>%USERPROFILE%/.weave/update-cache.json</c>. The cache
/// is read synchronously on every launch; a background refresh runs only
/// when the cache is stale or missing, so the reminder you see today was
/// discovered on a prior launch — startup is never blocked on the network.
///
/// Disable with <c>WEAVE_NO_UPDATE_CHECK=1</c>.
/// </summary>
internal static class VersionInfo
{
    public const string PackageId = "Weave.Cli";
    public const string UpgradeCommand = "dotnet tool update --global Weave.Cli";

    private const string NuGetIndexUrl = "https://api.nuget.org/v3-flatcontainer/weave.cli/index.json";
    private static readonly TimeSpan _cacheTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan _networkTimeout = TimeSpan.FromSeconds(3);

    private static readonly string _weaveHome = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave");
    private static readonly string _cachePath = Path.Combine(_weaveHome, "update-cache.json");

    public static string Current()
    {
        var asm = typeof(VersionInfo).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    public static bool IsEnabled()
    {
        var env = Environment.GetEnvironmentVariable("WEAVE_NO_UPDATE_CHECK");
        return !(string.Equals(env, "1", StringComparison.Ordinal)
            || string.Equals(env, "true", StringComparison.OrdinalIgnoreCase));
    }

    public static UpdateCache? LoadCache()
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

    public static void SaveCache(UpdateCache cache)
    {
        try
        {
            Directory.CreateDirectory(_weaveHome);
            var json = JsonSerializer.Serialize(cache, VersionJsonContext.Default.UpdateCache);
            File.WriteAllText(_cachePath, json);
        }
        catch
        {
            // best-effort — a read-only home dir should not crash the CLI
        }
    }

    /// <summary>
    /// Returns the cached "latest" version if it's newer than the
    /// currently-installed one. Pure: no network.
    /// </summary>
    public static string? PendingUpdateFromCache()
    {
        var cache = LoadCache();
        if (cache is null || string.IsNullOrWhiteSpace(cache.LatestVersion))
            return null;

        var current = Current();
        return IsNewer(cache.LatestVersion, current) ? cache.LatestVersion : null;
    }

    /// <summary>
    /// Fires a background refresh if the cache is missing or stale. Never
    /// throws; never awaits the returned task. Result is visible on the
    /// next launch (or to <see cref="CheckAsync"/>).
    /// </summary>
    public static void KickOffRefreshIfStale()
    {
        if (!IsEnabled())
            return;

        var cache = LoadCache();
        if (cache is not null && DateTimeOffset.UtcNow - cache.CheckedAt < _cacheTtl)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                var latest = await FetchLatestAsync(CancellationToken.None);
                if (latest is not null)
                    SaveCache(new UpdateCache { LatestVersion = latest, CheckedAt = DateTimeOffset.UtcNow });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"Weave update check failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Awaited check: hits NuGet now, updates the cache, returns a
    /// structured result suitable for printing.
    /// </summary>
    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken ct)
    {
        var current = Current();
        if (!IsEnabled())
            return new UpdateCheckResult(current, null, DateTimeOffset.UtcNow, false, "Update checks are disabled (WEAVE_NO_UPDATE_CHECK).");

        try
        {
            var latest = await FetchLatestAsync(ct);
            if (latest is null)
                return new UpdateCheckResult(current, null, DateTimeOffset.UtcNow, false, "Could not reach NuGet.");

            SaveCache(new UpdateCache { LatestVersion = latest, CheckedAt = DateTimeOffset.UtcNow });
            var newer = IsNewer(latest, current);
            return new UpdateCheckResult(current, latest, DateTimeOffset.UtcNow, newer, null);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(current, null, DateTimeOffset.UtcNow, false, ex.Message);
        }
    }

    private static async Task<string?> FetchLatestAsync(CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = _networkTimeout };
        using var response = await client.GetAsync(NuGetIndexUrl, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("versions", out var versions)
            || versions.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? max = null;
        foreach (var element in versions.EnumerateArray())
        {
            var v = element.GetString();
            if (string.IsNullOrWhiteSpace(v))
                continue;

            // Skip pre-releases for the "latest stable" reminder
            if (v.Contains('-', StringComparison.Ordinal))
                continue;

            if (max is null || IsNewer(v, max))
                max = v;
        }

        return max;
    }

    public static bool IsNewer(string candidate, string baseline)
    {
        static Version Parse(string s)
        {
            var dash = s.IndexOf('-', StringComparison.Ordinal);
            var plus = s.IndexOf('+', StringComparison.Ordinal);
            var cut = -1;
            if (dash >= 0)
                cut = dash;
            if (plus >= 0 && (cut == -1 || plus < cut))
                cut = plus;
            var numeric = cut >= 0 ? s[..cut] : s;
            return Version.TryParse(numeric, out var v) ? v : new Version(0, 0, 0);
        }

        return Parse(candidate).CompareTo(Parse(baseline)) > 0;
    }
}

internal sealed record UpdateCache
{
    public required string LatestVersion { get; init; }
    public required DateTimeOffset CheckedAt { get; init; }
}

internal sealed record UpdateCheckResult(
    string Current,
    string? Latest,
    DateTimeOffset CheckedAt,
    bool UpdateAvailable,
    string? Note);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UpdateCache))]
internal sealed partial class VersionJsonContext : JsonSerializerContext;
