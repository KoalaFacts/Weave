using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace Weave.Silo.Tests.Dashboard;

internal sealed class DashboardPlaywrightBrowser(Uri origin, Action<string>? onWebSocketClosed = null) : IAsyncDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;
    private readonly ConcurrentBag<Task> _webSocketClosures = [];

    public IPage Page => _page.ShouldNotBeNull();
    public ConcurrentQueue<string> BlockedRequests { get; } = new();

    public async Task StartAsync()
    {
        origin.IsLoopback.ShouldBeTrue("Only the owned Dashboard origin may be requested by the browser.");
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new()
        {
            Channel = "chromium",
            ChromiumSandbox = true,
            Timeout = 20_000
        });
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        _context = await _browser.NewContextAsync(new()
        {
            ServiceWorkers = ServiceWorkerPolicy.Block,
            ViewportSize = new() { Width = 1440, Height = 1000 }
        });
        _context.SetDefaultTimeout(20_000);
        await _context.RouteAsync("**/*", async route =>
        {
            if (!Permitted(route.Request.Url, webSocket: false))
            {
                BlockedRequests.Enqueue(route.Request.Url);
                await route.AbortAsync("blockedbyclient");
                return;
            }
            // Routing does not re-run for every redirected URL. Never let an owned
            // origin redirect the request (or its headers) outside this test boundary.
            var response = await route.FetchAsync(new() { MaxRedirects = 0, MaxRetries = 0, Timeout = 20_000 });
            try
            {
                if (response.Status is >= 300 and < 400)
                {
                    BlockedRequests.Enqueue("Redirect from " + route.Request.Url);
                    await route.AbortAsync("blockedbyclient");
                }
                else
                    await route.FulfillAsync(new() { Response = response });
            }
            finally
            {
                await response.DisposeAsync();
            }
        });
        await _context.RouteWebSocketAsync("**/*", socket =>
        {
            if (Permitted(socket.Url, webSocket: true))
            {
                var server = socket.ConnectToServer();
                if (onWebSocketClosed is not null)
                    socket.OnClose((code, reason) =>
                    {
                        // OnClose replaces the SDK's automatic forwarding to the server.
                        _webSocketClosures.Add(server.CloseAsync(new() { Code = code, Reason = reason }));
                        onWebSocketClosed(socket.Url);
                    });
            }
            else
            {
                BlockedRequests.Enqueue(socket.Url);
                _webSocketClosures.Add(socket.CloseAsync());
            }
        });
        // Observe native listener registration before the first upload; visibility alone
        // does not mean a server-rendered Blazor InputFile is interactive yet.
        await _context.AddInitScriptAsync("""
            (() => {
              const ready = new WeakSet();
              const add = EventTarget.prototype.addEventListener;
              EventTarget.prototype.addEventListener = function(type, listener, options) {
                const result = add.call(this, type, listener, options);
                if (type === 'change' && this instanceof HTMLInputElement && this.type === 'file') ready.add(this);
                return result;
              };
              globalThis.__weaveFileInputReady = element => !!element && ready.has(element);
            })();
            """);
        _page = await _context.NewPageAsync();
        await Page.GotoAsync(new Uri(origin, "/approvals/review").AbsoluteUri).WaitAsync(TestContext.Current.CancellationToken);
        await Page.Locator("#review-file").WaitForAsync().WaitAsync(TestContext.Current.CancellationToken);
        await using var ready = await Page.WaitForFunctionAsync("() => globalThis.__weaveFileInputReady(document.querySelector('#review-file'))")
            .WaitAsync(TestContext.Current.CancellationToken);
    }

    private bool Permitted(string address, bool webSocket) => Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && uri.Scheme == (webSocket ? "ws" : origin.Scheme) && uri.Host == origin.Host && uri.Port == origin.Port;

    public async ValueTask DisposeAsync()
    {
        try
        {
            try
            {
                await Task.WhenAll(_webSocketClosures).WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                try
                {
                    if (_context is not null)
                        await _context.CloseAsync();
                }
                finally
                {
                    // Closing the context can queue owned server-side socket closures.
                    await Task.WhenAll(_webSocketClosures).WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
        }
        finally
        {
            try
            {
                if (_browser is not null)
                    await _browser.CloseAsync();
            }
            finally
            {
                _playwright?.Dispose();
            }
        }
    }
}
