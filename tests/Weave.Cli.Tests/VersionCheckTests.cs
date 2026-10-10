using System.Net;
using Microsoft.Extensions.Time.Testing;

namespace Weave.Cli.Tests;

[Collection(nameof(ShellConsoleGroup))]
public sealed class VersionCheckTests
{
    [Theory]
    [InlineData("{\"versions\":[\"1.0.0-beta\",\"\",\"9999.0.0\",\"2.0.0\"]}", "9999.0.0", true)]
    [InlineData("{\"versions\":[\"0.0.0\"]}", "0.0.0", false)]
    public async Task CheckAsync_StableFeed_PersistsResultWithInjectedTime(string feed, string latest, bool available)
    {
        var previous = Environment.GetEnvironmentVariable("WEAVE_NO_UPDATE_CHECK");
        try
        {
            Environment.SetEnvironmentVariable("WEAVE_NO_UPDATE_CHECK", null);
            using var directory = new LocalTestDirectory();
            var cache = Path.Join(directory.Documents, "update.json");
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
            using var handler = new FeedHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(feed) });
            using var client = new HttpClient(handler);

            var result = await new VersionService(time).CheckAsync(cache, client, new Uri("https://feed.test/index.json"), TestContext.Current.CancellationToken);

            result.Current.ShouldBe(VersionService.Current());
            result.Latest.ShouldBe(latest);
            result.UpdateAvailable.ShouldBe(available);
            result.CheckedAt.ShouldBe(time.GetUtcNow());
            result.Note.ShouldBeNull();
            var stored = VersionService.LoadCache(cache).ShouldNotBeNull();
            stored.LatestVersion.ShouldBe(latest);
            stored.CheckedAt.ShouldBe(time.GetUtcNow());
            handler.RequestUri.ShouldNotBeNull().AbsoluteUri.ShouldBe("https://feed.test/index.json");
        }
        finally
        {
            Environment.SetEnvironmentVariable("WEAVE_NO_UPDATE_CHECK", previous);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"versions\":{}}")]
    [InlineData("{\"versions\":[\"1.0.0-preview\",\"\",null]}")]
    public async Task CheckAsync_NoStableVersion_PreservesExistingCache(string feed)
    {
        var previous = Environment.GetEnvironmentVariable("WEAVE_NO_UPDATE_CHECK");
        try
        {
            Environment.SetEnvironmentVariable("WEAVE_NO_UPDATE_CHECK", null);
            using var directory = new LocalTestDirectory();
            var cache = Path.Join(directory.Documents, "update.json");
            await File.WriteAllTextAsync(cache, "existing-cache-sentinel", TestContext.Current.CancellationToken);
            using var client = new HttpClient(new FeedHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(feed) }));

            var result = await new VersionService(TimeProvider.System).CheckAsync(cache, client, new Uri("https://feed.test/index.json"), TestContext.Current.CancellationToken);

            result.Latest.ShouldBeNull();
            result.UpdateAvailable.ShouldBeFalse();
            result.Note.ShouldBe("Could not reach NuGet.");
            (await File.ReadAllTextAsync(cache, TestContext.Current.CancellationToken)).ShouldBe("existing-cache-sentinel");
        }
        finally
        {
            Environment.SetEnvironmentVariable("WEAVE_NO_UPDATE_CHECK", previous);
        }
    }

    [Fact]
    public async Task CheckAsync_TransportFailure_ReturnsDiagnosticWithoutCreatingCache()
    {
        var previous = Environment.GetEnvironmentVariable("WEAVE_NO_UPDATE_CHECK");
        try
        {
            Environment.SetEnvironmentVariable("WEAVE_NO_UPDATE_CHECK", null);
            using var directory = new LocalTestDirectory();
            var cache = Path.Join(directory.Documents, "update.json");
            using var client = new HttpClient(new FeedHandler(() => throw new HttpRequestException("feed-unavailable-fixture")));

            var result = await new VersionService(TimeProvider.System).CheckAsync(cache, client, new Uri("https://feed.test/index.json"), TestContext.Current.CancellationToken);

            result.Latest.ShouldBeNull();
            result.UpdateAvailable.ShouldBeFalse();
            result.Note.ShouldBe("feed-unavailable-fixture");
            File.Exists(cache).ShouldBeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("WEAVE_NO_UPDATE_CHECK", previous);
        }
    }

    [Theory]
    [InlineData("1")]
    [InlineData("TrUe")]
    public async Task CheckAsync_Disabled_DoesNotReadFeedOrCreateCache(string disabled)
    {
        var previous = Environment.GetEnvironmentVariable("WEAVE_NO_UPDATE_CHECK");
        try
        {
            Environment.SetEnvironmentVariable("WEAVE_NO_UPDATE_CHECK", disabled);
            using var directory = new LocalTestDirectory();
            var cache = Path.Join(directory.Documents, "update.json");
            using var handler = new FeedHandler(() => throw new InvalidOperationException("Disabled check contacted feed"));
            using var client = new HttpClient(handler);

            var result = await new VersionService(TimeProvider.System).CheckAsync(cache, client, new Uri("https://feed.test/index.json"), TestContext.Current.CancellationToken);

            result.Note.ShouldNotBeNull().ShouldContain("disabled");
            result.UpdateAvailable.ShouldBeFalse();
            result.Latest.ShouldBeNull();
            handler.RequestUri.ShouldBeNull();
            File.Exists(cache).ShouldBeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("WEAVE_NO_UPDATE_CHECK", previous);
        }
    }

    private sealed class FeedHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(respond());
        }
    }
}
