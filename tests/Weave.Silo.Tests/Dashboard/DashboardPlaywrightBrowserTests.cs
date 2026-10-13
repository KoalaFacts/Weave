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
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await fixture.StartAsync(address =>
        {
            if (new Uri(address).PathAndQuery == "/_blazor?weave-reload-probe")
                closed.TrySetResult();
        });

        // A direct SignalR handshake establishes an owned socket whose lifetime this test
        // controls, independently of Blazor's transport negotiation and fallback.
        var socket = await OpenOwnedProbeAsync(fixture.Page);
        socket.IsClosed.ShouldBeFalse();

        // Reload closes routed sockets without a close code or reason in the driver protocol.
        await fixture.Page.ReloadAsync().WaitAsync(TestContext.Current.CancellationToken);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await using var ready = await fixture.Page.WaitForFunctionAsync(
            "() => globalThis.__weaveFileInputReady(document.querySelector('#review-file'))")
            .WaitAsync(TestContext.Current.CancellationToken);
        await fixture.ClickClearAsync();

        await Assertions.Expect(fixture.Page.Locator(".review-page"))
            .ToContainTextAsync("No file loaded. Select the retained original JSON request.");
        fixture.BlockedRequests.ShouldBeEmpty();
        fixture.Api.Requests.ShouldBeEmpty();
    }

    private static async Task<IWebSocket> OpenOwnedProbeAsync(IPage page)
    {
        var nativeError = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        IWebSocket? observedSocket = null;
        var httpRouteCount = 0;
        Func<string, bool> matchesProbe = address => new Uri(address).PathAndQuery == "/_blazor?weave-reload-probe";
        Task ObserveHttpRouteAsync(IRoute route)
        {
            Interlocked.Increment(ref httpRouteCount);
            // Preserve the existing context origin and redirect checks.
            return route.FallbackAsync();
        }
        void CaptureSocketError(object? sender, string error) => nativeError.TrySetResult(error[..Math.Min(error.Length, 512)]);
        void ObserveSocket(object? sender, IWebSocket connection)
        {
            if (matchesProbe(connection.Url))
            {
                observedSocket = connection;
                connection.SocketError += CaptureSocketError;
            }
        }

        await page.RouteAsync(matchesProbe, ObserveHttpRouteAsync);
        page.WebSocket += ObserveSocket;
        try
        {
            return await page.RunAndWaitForWebSocketAsync(async () =>
            {
                var handshake = await page.EvaluateAsync<string>("""
                () => new Promise((resolve, reject) => {
                  const url = new URL('/_blazor?weave-reload-probe', location.href);
                  url.protocol = 'ws:';
                  const socket = globalThis.__weaveReloadProbe = new WebSocket(url);
                  socket.binaryType = 'arraybuffer';
                  socket.onopen = () => socket.send(JSON.stringify({ protocol: 'blazorpack', version: 1 }) + '\u001e');
                  socket.onmessage = event => resolve(typeof event.data === 'string'
                    ? event.data : new TextDecoder().decode(event.data));
                  socket.onerror = () => reject(new Error('Owned Blazor WebSocket handshake failed.'));
                  socket.onclose = () => reject(new Error('Owned Blazor WebSocket closed before its handshake.'));
                })
                """).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                handshake.ShouldBe("{}\u001e");
            }, new()
            {
                Predicate = connection => matchesProbe(connection.Url)
            }).WaitAsync(TestContext.Current.CancellationToken);
        }
        catch (PlaywrightException error)
        {
            // Native Chromium errors distinguish an HTTP rejection from browser policy.
            // Retain only this probe's bounded error, with no headers or response bodies.
            await Task.WhenAny(nativeError.Task, Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var detail = nativeError.Task.IsCompletedSuccessfully ? await nativeError.Task : "not reported within 2 seconds";
            throw new PlaywrightException(
                $"Owned WebSocket failed; native error: {detail}; HTTP route observations: {Volatile.Read(ref httpRouteCount)}.", error);
        }
        finally
        {
            page.WebSocket -= ObserveSocket;
            if (observedSocket is not null)
                observedSocket.SocketError -= CaptureSocketError;
            await page.UnrouteAsync(matchesProbe, ObserveHttpRouteAsync);
        }
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
