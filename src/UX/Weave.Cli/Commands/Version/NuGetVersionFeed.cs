using System.Text.Json;

namespace Weave.Cli.Commands;

internal sealed class NuGetVersionFeed
{
    private const string NuGetIndexUrl = "https://api.nuget.org/v3-flatcontainer/weave.cli/index.json";
    private static readonly TimeSpan _networkTimeout = TimeSpan.FromSeconds(3);

    private readonly HttpClient _client;
    private readonly Uri _indexUri;

    public NuGetVersionFeed()
        : this(new HttpClient { Timeout = _networkTimeout }, new Uri(NuGetIndexUrl))
    {
    }

    internal NuGetVersionFeed(HttpClient client, Uri indexUri)
    {
        _client = client;
        _indexUri = indexUri;
    }

    public async Task<string?> FetchLatestAsync(CancellationToken ct)
    {
        using var response = await _client.GetAsync(_indexUri, ct);
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

            if (latest is null || VersionComparer.IsNewer(version, latest))
                latest = version;
        }

        return latest;
    }
}
