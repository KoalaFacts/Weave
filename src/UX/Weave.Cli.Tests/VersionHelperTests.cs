using System.Globalization;
using System.Net;
using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

public sealed class VersionHelperTests
{
    [Theory]
    [InlineData("1.2.4", "1.2.3", true)]
    [InlineData("1.2.3", "1.2.3", false)]
    [InlineData("1.2.3-beta.1", "1.2.2", true)]
    [InlineData("1.2.3+build.7", "1.2.2", true)]
    [InlineData("not-a-version", "1.0.0", false)]
    public void IsNewer_ParsesVersionStrings(string candidate, string baseline, bool expected)
    {
        VersionService.IsNewer(candidate, baseline).ShouldBe(expected);
    }

    [Fact]
    public void CacheStore_SaveThenLoad_RoundTripsCache()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "weave-tests", Guid.NewGuid().ToString("N"));
        var cachePath = Path.Combine(testRoot, "update-cache.json");
        var cache = new UpdateCache
        {
            LatestVersion = "1.2.3",
            CheckedAt = DateTimeOffset.Parse("2026-05-01T00:00:00Z", CultureInfo.InvariantCulture)
        };

        try
        {
            VersionService.SaveCache(cachePath, cache);

            var loaded = VersionService.LoadCache(cachePath);

            loaded.ShouldNotBeNull();
            loaded!.LatestVersion.ShouldBe("1.2.3");
            loaded.CheckedAt.ShouldBe(cache.CheckedAt);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void CacheStore_InvalidJson_ReturnsNull()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "weave-tests", Guid.NewGuid().ToString("N"));
        var cachePath = Path.Combine(testRoot, "update-cache.json");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllText(cachePath, "not json");

            VersionService.LoadCache(cachePath).ShouldBeNull();
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Feed_FetchLatestAsync_ReturnsHighestStableVersion()
    {
        using var client = CreateClient("""
            { "versions": ["1.0.0", "1.1.0-beta.1", "1.0.2", "2.0.0"] }
            """);

        var latest = await VersionService.FetchLatestAsync(
            client,
            new Uri("https://example.test/index.json"),
            TestContext.Current.CancellationToken);

        latest.ShouldBe("2.0.0");
    }

    [Fact]
    public async Task Feed_FetchLatestAsync_NonSuccessStatus_ReturnsNull()
    {
        using var client = CreateClient("{}", HttpStatusCode.ServiceUnavailable);

        var latest = await VersionService.FetchLatestAsync(
            client,
            new Uri("https://example.test/index.json"),
            TestContext.Current.CancellationToken);

        latest.ShouldBeNull();
    }

    private static HttpClient CreateClient(string json, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(new StubHandler(statusCode, json));

    private sealed class StubHandler(HttpStatusCode statusCode, string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content)
            });
        }
    }
}
