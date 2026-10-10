using System.Net;
using Weave.Actions.Dashboard;
using Weave.Actions.SystemInfo;

namespace Weave.Cli.Tests;

[Collection(nameof(ShellConsoleGroup))]
public sealed class WebUiStatusCommandTests
{
    [Theory]
    [InlineData(200, true)]
    [InlineData(401, true)]
    [InlineData(503, false)]
    public async Task ExecuteAsync_NoOpen_ReportsExplicitDashboardReachability(int status, bool reachable)
    {
        using var output = new ShellOutputCapture();
        using var handler = new DashboardHandler((HttpStatusCode)status);
        using var client = new HttpClient(handler);
        var config = Substitute.For<ISystemConfigSource>();
        var command = new WebUiCliCommand(new GetDashboardStatusAction(config, client));

        var result = await command.ExecuteAsync(new WebUiOptions("https://dashboard.test/custom", NoOpen: true), TestContext.Current.CancellationToken);

        result.ShouldBe(0);
        handler.RequestUri.ShouldNotBeNull().AbsoluteUri.ShouldBe("https://dashboard.test/custom");
        output.Text.ShouldContain("Web UI: https://dashboard.test/custom");
        if (reachable)
        {
            output.Text.ShouldContain("Dashboard is reachable.");
            output.Text.ShouldNotContain("not reachable");
        }
        else
        {
            output.Text.ShouldContain("Dashboard is not reachable yet.");
            output.Text.ShouldNotContain("Dashboard is reachable.");
        }
        output.Text.ShouldNotContain("Opened in your default browser.");
        output.Text.ShouldNotContain("Could not open a browser");
        config.DidNotReceive().Load();
    }

    private sealed class DashboardHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
