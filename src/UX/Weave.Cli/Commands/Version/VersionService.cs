using System.Reflection;

namespace Weave.Cli.Commands;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance service keeps version behavior replaceable from CLI commands.")]
internal sealed class VersionService
{
    public const string UpgradeCommand = "dotnet tool update --global Weave.Cli";

    private static readonly TimeSpan _cacheTtl = TimeSpan.FromHours(24);

    private readonly VersionCacheStore _cacheStore;
    private readonly NuGetVersionFeed _feed;
    private readonly VersionComparer _comparer;

    public VersionService()
        : this(new VersionCacheStore(), new NuGetVersionFeed(), new VersionComparer())
    {
    }

    internal VersionService(VersionCacheStore cacheStore, NuGetVersionFeed feed, VersionComparer comparer)
    {
        _cacheStore = cacheStore;
        _feed = feed;
        _comparer = comparer;
    }

    public string Current()
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

    public bool IsEnabled()
    {
        var value = Environment.GetEnvironmentVariable("WEAVE_NO_UPDATE_CHECK");
        return !(string.Equals(value, "1", StringComparison.Ordinal)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
    }

    public UpdateCache? LoadCache() => _cacheStore.Load();

    public string? PendingUpdateFromCache()
    {
        var cache = _cacheStore.Load();
        if (cache is null || string.IsNullOrWhiteSpace(cache.LatestVersion))
            return null;

        var current = Current();
        return _comparer.IsNewer(cache.LatestVersion, current) ? cache.LatestVersion : null;
    }

    public void KickOffRefreshIfStale()
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

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct)
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
            var newer = _comparer.IsNewer(latest, current);
            return new UpdateCheckResult(current, latest, DateTimeOffset.UtcNow, newer, null);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(current, null, DateTimeOffset.UtcNow, false, ex.Message);
        }
    }

    public bool IsNewer(string candidate, string baseline) => _comparer.IsNewer(candidate, baseline);
}
