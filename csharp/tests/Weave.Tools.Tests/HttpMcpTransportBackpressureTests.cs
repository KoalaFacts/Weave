using System.Net;
using System.Text;
using Weave.Tools.Connectors;
using Weave.Workspaces.Manifest;

namespace Weave.Tools.Tests;

public sealed class HttpMcpTransportBackpressureTests
{
    [Fact]
    public async Task SendAsync_ResponseQueueIsFull_DeadlineReleasesBlockedWriter()
    {
        using var client = new HttpClient(new FramesHandler());
        await using var transport = HttpMcpTransport.CreateForTesting(client, new McpConfig
        {
            Url = "https://example.test/mcp",
            MaxQueuedFrames = 1,
            RequestTimeoutSeconds = 1
        });
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(4));

        var error = await Record.ExceptionAsync(() => transport.SendAsync("{}", watchdog.Token));

        error.ShouldBeOfType<IOException>().Message.ShouldContain("timed out");
        watchdog.IsCancellationRequested.ShouldBeFalse();
        (await transport.ReceiveAsync(TestContext.Current.CancellationToken)).ShouldBe("first");
    }

    private sealed class FramesHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: first\n\ndata: second\n\n", Encoding.UTF8, "text/event-stream")
            });
    }
}
