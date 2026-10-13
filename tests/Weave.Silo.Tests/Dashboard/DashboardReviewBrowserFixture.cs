using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Weave.Silo.Tests.Invocations;

namespace Weave.Silo.Tests.Dashboard;

internal sealed partial class DashboardReviewBrowserFixture : IAsyncDisposable
{
    private DashboardBrowserChild? _dashboard;
    private DashboardPlaywrightBrowser? _browser;

    public DashboardReviewBrowserFixture(bool hold = false, bool deny = false)
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Dashboard browser tests require Linux process/profile isolation.");
        Root = Directory.CreateTempSubdirectory("weave-dashboard-browser-").FullName;
        Api = new DashboardReviewBrowserApi(Root, hold, deny);
    }

    public string Root { get; }
    public DashboardReviewBrowserApi Api { get; }
    public IPage Page => _browser.ShouldNotBeNull().Page;
    public IReadOnlyCollection<string> BlockedRequests => _browser.ShouldNotBeNull().BlockedRequests.ToArray();

    public async Task StartAsync(Action<string>? onWebSocketClosed = null)
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

        _browser = new DashboardPlaywrightBrowser(address, onWebSocketClosed);
        await _browser.StartAsync();
    }

    public Task FillAsync(string id, string value) =>
        Page.Locator("#" + id).FillAsync(value).WaitAsync(TestContext.Current.CancellationToken);

    public async Task LoadRequestAsync(string path)
    {
        await FillAsync("review-workspace", "release-lab");
        await FillAsync("review-tool", "files");
        await Page.Locator("#review-file").SetInputFilesAsync(path).WaitAsync(TestContext.Current.CancellationToken);
        await Assertions.Expect(Page.Locator(".review-page")).ToContainTextAsync("Not yet verified; no file is saved by the Dashboard.")
            .WaitAsync(TestContext.Current.CancellationToken);
        await FillAsync("review-capability", "browser-review-capability");
        await FillAsync("review-api-token", "browser-global-token");
    }

    public Task ClickVerifyAsync() => Page.GetByRole(AriaRole.Button, new() { Name = "Verify for review", Exact = true })
        .ClickAsync().WaitAsync(TestContext.Current.CancellationToken);
    public Task ClickClearAsync() => Page.GetByRole(AriaRole.Button, new() { Name = "Clear and cancel", Exact = true })
        .ClickAsync().WaitAsync(TestContext.Current.CancellationToken);
    public Task<string> TextAsync() => Page.Locator(".review-page").InnerTextAsync().WaitAsync(TestContext.Current.CancellationToken);

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
                if (_dashboard is not null)
                    await _dashboard.DisposeAsync();
            }
            finally
            {
                await DisposeApiAndRootAsync();
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
