using Microsoft.Playwright;

namespace Weave.Silo.Tests.Dashboard;

[Trait("Category", "Integration")]
public sealed class DashboardPlaywrightBrowserTests
{
    [Fact]
    public async Task Browser_RequestsAnotherLoopbackOrigin_BlocksHttpAndWebSocketBeforeDispatch()
    {
        await using var fixture = new DashboardReviewBrowserFixture();
        await fixture.StartAsync();
        var address = new Uri(fixture.Api.Address, "/browser-must-not-contact-upstream").AbsoluteUri;

        (await fixture.Page.EvaluateAsync<bool>("async url => { try { await fetch(url); return false; } catch { return true; } }", address))
            .ShouldBeTrue();
        await fixture.Page.EvaluateAsync("""
            url => new Promise(resolve => {
              const socket = new WebSocket(url.replace('http:', 'ws:'));
              socket.onclose = () => resolve(true);
            })
            """, address).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        fixture.BlockedRequests.ShouldContain(address);
        fixture.BlockedRequests.ShouldContain(address.Replace("http:", "ws:", StringComparison.Ordinal));
        await Assertions.Expect(fixture.Page.Locator("#review-file")).ToBeVisibleAsync();
        await fixture.ClickClearAsync();
        await Assertions.Expect(fixture.Page.Locator(".review-page"))
            .ToContainTextAsync("No file loaded. Select the retained original JSON request.");
        fixture.Api.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Browser_ReloadsOwnedPage_ClosesRoutedWebSocketAndRemainsInteractive()
    {
        await using var fixture = new DashboardReviewBrowserFixture();
        await fixture.StartAsync();

        // Reload closes routed sockets without a close code or reason in the driver protocol.
        // The SDK must handle that event without poisoning the shared Playwright connection.
        var socket = await fixture.Page.RunAndWaitForWebSocketAsync(() => fixture.Page.ReloadAsync(), new()
        {
            Predicate = connection => new Uri(connection.Url).AbsolutePath == "/_blazor"
        }).WaitAsync(TestContext.Current.CancellationToken);
        await using var ready = await fixture.Page.WaitForFunctionAsync(
            "() => globalThis.__weaveFileInputReady(document.querySelector('#review-file'))")
            .WaitAsync(TestContext.Current.CancellationToken);
        await fixture.ClickClearAsync();

        await Assertions.Expect(fixture.Page.Locator(".review-page"))
            .ToContainTextAsync("No file loaded. Select the retained original JSON request.");
        socket.IsClosed.ShouldBeFalse();
        fixture.BlockedRequests.ShouldBeEmpty();
        fixture.Api.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Browser_DisposedDuringPendingPageOperation_ClosesPageAndRemovesTemporaryState()
    {
        var fixture = new DashboardReviewBrowserFixture();
        IPage? page = null;
        Task? pending = null;
        try
        {
            await fixture.StartAsync();
            page = fixture.Page;
            pending = page.Locator("#element-that-will-never-exist").WaitForAsync(new() { Timeout = 60_000 });
            pending.IsCompleted.ShouldBeFalse();
        }
        finally
        {
            await fixture.DisposeAsync();
        }

        page.ShouldNotBeNull().IsClosed.ShouldBeTrue();
        await Should.ThrowAsync<PlaywrightException>(pending.ShouldNotBeNull().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Directory.Exists(fixture.Root).ShouldBeFalse();
    }
}
