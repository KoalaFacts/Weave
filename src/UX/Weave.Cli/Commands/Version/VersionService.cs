using System.Reflection;

namespace Weave.Cli.Commands;

internal static class VersionService
{
    public const string UpgradeCommand = "dotnet tool update --global Weave.Cli";

    private static readonly TimeSpan _cacheTtl = TimeSpan.FromHours(24);
    private static readonly VersionCacheStore _cacheStore = new();
    private static readonly NuGetVersionFeed _feed = new();

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

    public static UpdateCache? LoadCache() => _cacheStore.Load();

    public static string? PendingUpdateFromCache()
    {
        var cache = _cacheStore.Load();
        if (cache is null || string.IsNullOrWhiteSpace(cache.LatestVersion))
            return null;

        var current = Current();
        return VersionComparer.IsNewer(cache.LatestVersion, current) ? cache.LatestVersion : null;
    }

    public static void KickOffRefreshIfStale()
    {
        if (!IsEnabled())
            return;

        var cache = _cacheStore.Load();
        if (cache is not null && DateTimeOffset.UtcNow - cache.CheckedAt < _cacheTtl)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                var latest = await _feed.FetchLatestAsync(CancellationToken.None);
                if (latest is not null)
                    _cacheStore.Save(new UpdateCache { LatestVersion = latest, CheckedAt = DateTimeOffset.UtcNow });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"Weave update check failed: {ex.Message}");
            }
        });
    }

    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken ct)
    {
        var current = Current();
        if (!IsEnabled())
        {
            return new UpdateCheckResult(
                current,
                null,
                DateTimeOffset.UtcNow,
                false,
                "Update checks are disabled (WEAVE_NO_UPDATE_CHECK).");
        }

        try
        {
            var latest = await _feed.FetchLatestAsync(ct);
            if (latest is null)
                return new UpdateCheckResult(current, null, DateTimeOffset.UtcNow, false, "Could not reach NuGet.");

            _cacheStore.Save(new UpdateCache { LatestVersion = latest, CheckedAt = DateTimeOffset.UtcNow });
            var newer = VersionComparer.IsNewer(latest, current);
            return new UpdateCheckResult(current, latest, DateTimeOffset.UtcNow, newer, null);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(current, null, DateTimeOffset.UtcNow, false, ex.Message);
        }
    }

    public static bool IsNewer(string candidate, string baseline) =>
        VersionComparer.IsNewer(candidate, baseline);
}
