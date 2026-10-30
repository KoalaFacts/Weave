using Weave.Tools.Connectors;
using Weave.Workspaces.Manifest;

namespace Weave.Tools.Tests;

public sealed class HttpMcpTransportWireBoundaryTests
{
    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task SendAsync_Redirect_NeverSendsToAnotherEndpoint(int status)
    {
        await using var target = new McpHttpTestPeer((stream, ct) => McpHttpTestPeer.ReplyAsync(stream, ct));
        await using var origin = new McpHttpTestPeer((stream, ct) => McpHttpTestPeer.ReplyAsync(stream, ct,
            extraHeaders: $"Location: {target.Endpoint}\r\n", status: status));
        await using var transport = await HttpMcpTransport.ConnectAsync(Config(origin), TestContext.Current.CancellationToken);

        var error = await Record.ExceptionAsync(() => transport.SendAsync("{\"privateInput\":\"synthetic-value\"}", TestContext.Current.CancellationToken));

        error.ShouldBeOfType<IOException>();
        origin.Requests.Count.ShouldBe(1);
        target.Requests.ShouldBeEmpty();
        error.Message.ShouldNotContain("synthetic-value");
    }

    [Fact]
    public async Task SendAsync_ServerCookie_DoesNotAddImplicitCredentialsToNextCall()
    {
        await using var peer = new McpHttpTestPeer((stream, ct) => McpHttpTestPeer.ReplyAsync(stream, ct,
            extraHeaders: "Set-Cookie: authority=unexpected; Path=/\r\n"));
        await using var transport = await HttpMcpTransport.ConnectAsync(Config(peer), TestContext.Current.CancellationToken);

        await transport.SendAsync("{}", TestContext.Current.CancellationToken);
        (await transport.ReceiveAsync(TestContext.Current.CancellationToken)).ShouldBe("{}");
        await transport.SendAsync("{}", TestContext.Current.CancellationToken);
        (await transport.ReceiveAsync(TestContext.Current.CancellationToken)).ShouldBe("{}");

        peer.Requests.Count.ShouldBe(2);
        foreach (var request in peer.Requests)
            request.Headers.ShouldNotContain("Cookie:");
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/event-stream")]
    public async Task SendAsync_HeadersArriveButBodyStalls_WholeRequestDeadlineStillApplies(string contentType)
    {
        await using var peer = new McpHttpTestPeer(async (stream, ct) =>
        {
            await McpHttpTestPeer.HeadersAsync(stream, ct, contentType);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });
        await using var transport = await HttpMcpTransport.ConnectAsync(Config(peer) with
        {
            RequestTimeoutSeconds = 1,
            IdleTimeoutSeconds = 10
        }, TestContext.Current.CancellationToken);
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(4));

        var error = await Record.ExceptionAsync(() => transport.SendAsync("{}", watchdog.Token));

        error.ShouldBeOfType<IOException>().Message.ShouldContain("timed out");
        watchdog.IsCancellationRequested.ShouldBeFalse();
        peer.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task DisposeAsync_BodyReadIsActive_InterruptsItWithoutWaitingForCallerDeadline()
    {
        var headersSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var peer = new McpHttpTestPeer(async (stream, ct) =>
        {
            await McpHttpTestPeer.HeadersAsync(stream, ct, "application/json");
            headersSent.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });
        await using var transport = await HttpMcpTransport.ConnectAsync(Config(peer), TestContext.Current.CancellationToken);
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(4));
        var sending = transport.SendAsync("{}", watchdog.Token);
        await headersSent.Task.WaitAsync(watchdog.Token);

        await transport.DisposeAsync();
        var error = await Record.ExceptionAsync(() => sending);

        error.ShouldBeOfType<IOException>();
        watchdog.IsCancellationRequested.ShouldBeFalse();
        peer.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SendAsync_CallerCancelsBodyRead_RemainsCancellationNotTimeoutOrSuccess()
    {
        var headersSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var peer = new McpHttpTestPeer(async (stream, ct) =>
        {
            await McpHttpTestPeer.HeadersAsync(stream, ct, "application/json");
            headersSent.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });
        await using var transport = await HttpMcpTransport.ConnectAsync(Config(peer), TestContext.Current.CancellationToken);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var sending = transport.SendAsync("{}", caller.Token);
        await headersSent.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        await caller.CancelAsync();
        var error = await Record.ExceptionAsync(() => sending);

        error.ShouldBeAssignableTo<OperationCanceledException>();
        peer.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SendAsync_PeerConsumesRequestThenCloses_ReportsFailureWithoutResending()
    {
        await using var peer = new McpHttpTestPeer((_, _) => Task.CompletedTask);
        await using var transport = await HttpMcpTransport.ConnectAsync(Config(peer), TestContext.Current.CancellationToken);

        await Should.ThrowAsync<IOException>(() => transport.SendAsync("{\"operation\":\"effect\"}", TestContext.Current.CancellationToken));

        peer.Requests.Count.ShouldBe(1);
        peer.Requests.Single().Body.ShouldBe("{\"operation\":\"effect\"}");
    }

    private static McpConfig Config(McpHttpTestPeer peer) => new()
    {
        Url = peer.Endpoint,
        AllowPrivateEndpoints = true,
        RequestTimeoutSeconds = 5
    };
}
