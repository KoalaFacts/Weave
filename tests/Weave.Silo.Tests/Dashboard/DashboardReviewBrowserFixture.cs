using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Weave.Silo.Tests.Invocations;

namespace Weave.Silo.Tests.Dashboard;

internal sealed partial class DashboardReviewBrowserFixture : IAsyncDisposable
{
    private DashboardBrowserChild? _dashboard;
    private DashboardBrowserChild? _chrome;
    private DashboardCdpClient? _browser;
    private readonly string _chromePath;

    public DashboardReviewBrowserFixture(bool hold = false, bool deny = false)
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Dashboard browser tests require Linux process/profile isolation.");
        _chromePath = FindChrome();
        Root = Directory.CreateTempSubdirectory("weave-dashboard-browser-").FullName;
        Api = new DashboardReviewBrowserApi(Root, hold, deny);
    }

    public string Root { get; }
    public DashboardReviewBrowserApi Api { get; }
    public DashboardCdpClient Browser => _browser.ShouldNotBeNull();

    public async Task StartAsync()
    {
        await Api.StartAsync();
        var assembly = DashboardReviewRenderingTests.DashboardType("Weave.Dashboard.Pages.Approvals.Review").Assembly.Location;
        var project = new DirectoryInfo(Path.GetDirectoryName(assembly).ShouldNotBeNull()).Parent.ShouldNotBeNull()
            .Parent.ShouldNotBeNull().Parent.ShouldNotBeNull().FullName;
        var start = DashboardBrowserChild.StartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet", Root);
        start.ArgumentList.Add(assembly);
        start.ArgumentList.Add("--contentRoot");
        start.ArgumentList.Add(project);
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add("http://127.0.0.1:0");
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["Weave__Review__Enabled"] = "true";
        start.Environment["Weave__Review__BaseUrl"] = Api.Address.AbsoluteUri;
        start.Environment["OTEL_EXPORTER_OTLP_ENDPOINT"] = "";
        start.Environment["Logging__LogLevel__Default"] = "Information";
        _dashboard = new DashboardBrowserChild(start);
        var address = await WaitForDashboardAsync(_dashboard);

        var profile = Directory.CreateDirectory(Path.Join(Root, "chrome-profile")).FullName;
        var browserStart = DashboardBrowserChild.StartInfo(_chromePath, Root);
        foreach (var argument in new[]
        {
            "--headless=new", "--remote-debugging-address=127.0.0.1", "--remote-debugging-port=0",
            "--user-data-dir=" + profile, "--no-first-run", "--no-default-browser-check",
            "--disable-background-networking", "--disable-component-update", "--disable-sync",
            "--disable-default-apps", "--disable-extensions", "--disable-dev-shm-usage", "--no-proxy-server",
            "--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE 127.0.0.1", "about:blank"
        })
            browserStart.ArgumentList.Add(argument);
        _chrome = new DashboardBrowserChild(browserStart);
        var endpoint = await WaitForChromeAsync(_chrome, profile);
        _browser = new DashboardCdpClient(address);
        await _browser.ConnectAsync(endpoint);
        await Browser.CommandAsync("Page.navigate", new JsonObject { ["url"] = new Uri(address, "/approvals/review").AbsoluteUri });
        await Browser.WaitForAsync("document.querySelector('#review-file') !== null", "Review input form");
        // The initial HTML can precede SignalR startup. An observable Clear response proves events are live.
        await Browser.WaitForAsync("""
            (() => {
              document.querySelector('.review-page .actions button:last-child')?.click();
              return document.querySelector('.review-page')?.innerText.includes('No file loaded. Select the retained original JSON request.') === true;
            })()
            """, "interactive Clear event");
    }

    public async Task FillAsync(string id, string value)
    {
        await Browser.EvaluateAsync<bool>("""
            (() => {
              const input = document.getElementById(ID);
              input.value = VALUE;
              input.dispatchEvent(new Event('input', { bubbles: true }));
              return true;
            })()
            """.Replace("ID", JsonSerializer.Serialize(id), StringComparison.Ordinal)
                .Replace("VALUE", JsonSerializer.Serialize(value), StringComparison.Ordinal));
    }

    public async Task LoadRequestAsync(string path)
    {
        await FillAsync("review-workspace", "release-lab");
        await FillAsync("review-tool", "files");
        await Browser.UploadAsync(path);
        await Browser.WaitForAsync("document.querySelector('.review-page').innerText.includes('Not yet verified; no file is saved by the Dashboard.')",
            "uploaded retained request");
        await FillAsync("review-capability", "browser-review-capability");
        await FillAsync("review-api-token", "browser-global-token");
    }

    public Task<bool> ClickVerifyAsync() => Browser.EvaluateAsync<bool>("document.querySelector('.review-page .actions button:first-child').click(); true");
    public Task<bool> ClickClearAsync() => Browser.EvaluateAsync<bool>("document.querySelector('.review-page .actions button:last-child').click(); true");
    public Task<string> TextAsync() => Browser.EvaluateAsync<string>("document.querySelector('.review-page').innerText");

    private static string FindChrome()
    {
        var names = new[] { "google-chrome", "google-chrome-stable", "chromium", "chromium-browser" };
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(path => path.Length != 0).SelectMany(path => names.Select(name => Path.Join(path, name))).ToList();
        var configured = Environment.GetEnvironmentVariable("CHROME_BIN");
        if (!string.IsNullOrWhiteSpace(configured))
            candidates.Insert(0, configured);
        return candidates.FirstOrDefault(File.Exists).ShouldNotBeNull(
            "Dashboard browser tests require installed Chrome/Chromium (or CHROME_BIN). CI ubuntu-latest includes Chrome; no browser is downloaded by tests.");
    }

    private static async Task<Uri> WaitForDashboardAsync(DashboardBrowserChild child)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            child.HasExited.ShouldBeFalse("Dashboard exited before listening: " + child.Log);
            var match = ListeningAddress().Match(child.Log);
            if (match.Success)
                return new Uri(match.Groups[1].Value);
            await Task.Delay(50, deadline.Token);
        }
    }

    private static async Task<Uri> WaitForChromeAsync(DashboardBrowserChild child, string profile)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var portFile = Path.Join(profile, "DevToolsActivePort");
        var port = 0;
        while (port == 0)
        {
            child.HasExited.ShouldBeFalse("Chrome exited before debugging was ready: " + child.Log);
            if (File.Exists(portFile))
            {
                var lines = await File.ReadAllLinesAsync(portFile, deadline.Token);
                int.TryParse(lines.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out port);
            }
            if (port == 0)
                await Task.Delay(50, deadline.Token);
        }
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        while (true)
        {
            child.HasExited.ShouldBeFalse("Chrome exited before creating a page: " + child.Log);
            var json = await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", deadline.Token);
            using var document = JsonDocument.Parse(json);
            foreach (var target in document.RootElement.EnumerateArray())
                if (target.GetProperty("type").GetString() == "page")
                    return new Uri(target.GetProperty("webSocketDebuggerUrl").GetString().ShouldNotBeNull());
            await Task.Delay(50, deadline.Token);
        }
    }

    [GeneratedRegex(@"Now listening on: (http://127\.0\.0\.1:\d+)")]
    private static partial Regex ListeningAddress();

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_browser is not null)
                await _browser.DisposeAsync();
        }
        finally
        {
            try
            {
                if (_chrome is not null)
                    await _chrome.DisposeAsync();
            }
            finally
            {
                try
                {
                    if (_dashboard is not null)
                        await _dashboard.DisposeAsync();
                }
                finally
                {
                    await DisposeApiAndRootAsync();
                }
            }
        }
    }

    private async Task DisposeApiAndRootAsync()
    {
        var errors = new List<Exception>();
        try
        {
            await Api.DisposeAsync();
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
        finally
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                errors.Add(error);
            }
        }
        if (errors.Count != 0)
            throw new AggregateException("Dashboard browser API/profile cleanup failed.", errors);
    }

}
