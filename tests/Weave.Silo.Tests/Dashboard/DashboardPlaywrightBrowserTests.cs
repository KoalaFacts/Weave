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
    public async Task Browser_ReloadsPage_DecodesRoutedSocketCloseWithoutMetadataAndRemainsInteractive()
    {
        await using var fixture = new DashboardReviewBrowserFixture();
        await fixture.StartAsync();
        var closed = new TaskCompletionSource<(int? Code, string? Reason)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeAddress = new UriBuilder(fixture.Page.Url)
        {
            Scheme = "ws",
            Path = "/__weave_playwright_reload_probe",
            Query = "",
            Fragment = ""
        }.Uri.AbsoluteUri;
        // This exact test-only route exercises the SDK's mock socket lifecycle without
        // changing the context routes for Dashboard traffic or foreign-origin denial.
        await fixture.Page.RouteWebSocketAsync(address => address == probeAddress,
            socket => socket.OnClose((code, reason) => closed.TrySetResult((code, reason))));
        (await fixture.Page.EvaluateAsync<bool>("""
            url => new Promise((resolve, reject) => {
              const socket = globalThis.__weaveReloadProbe = new WebSocket(url);
              socket.onopen = () => resolve(socket.readyState === WebSocket.OPEN);
              socket.onerror = () => reject(new Error('Test routed WebSocket failed to open.'));
              socket.onclose = () => reject(new Error('Test routed WebSocket closed before opening.'));
            })
            """, probeAddress).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ShouldBeTrue();

        // Destroying the page realm emits closePage with neither code nor reason.
        // SDK 1.62.0 threw before invoking this callback; 1.63.0 decodes both as null.
        await fixture.Page.ReloadAsync().WaitAsync(TestContext.Current.CancellationToken);
        var close = await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        close.Code.ShouldBeNull();
        close.Reason.ShouldBeNull();
        await using var ready = await fixture.Page.WaitForFunctionAsync(
            "() => globalThis.__weaveFileInputReady(document.querySelector('#review-file'))")
            .WaitAsync(TestContext.Current.CancellationToken);
        await fixture.ClickClearAsync();

        await Assertions.Expect(fixture.Page.Locator(".review-page"))
            .ToContainTextAsync("No file loaded. Select the retained original JSON request.");
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
