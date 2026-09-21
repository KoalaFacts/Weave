using System.Text;
using Weave.Tools.Connectors;
using Weave.Workspaces.Manifest;

namespace Weave.Tools.Tests;

public sealed class HttpMcpTransportWireRoundTripTests
{
    [Theory]
    [InlineData("application/json")]
    [InlineData("text/event-stream")]
    public async Task SendAsync_RepeatedNativeUtf8RoundTrips_PreservesFrameAndOneRequestPerCall(string contentType)
    {
        const string frame = "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":\"汉🙂\"}";
        var body = Encoding.UTF8.GetBytes(contentType == "application/json" ? frame : $"data: {frame}\n\n");
        await using var peer = new McpHttpTestPeer(async (stream, ct) =>
        {
            await McpHttpTestPeer.HeadersAsync(stream, ct, contentType, body.Length);
            await stream.WriteAsync(body, ct);
        });
        await using var transport = await HttpMcpTransport.ConnectAsync(new McpConfig
        {
            Url = peer.Endpoint,
            AllowPrivateEndpoints = true,
            MaxFrameBytes = Encoding.UTF8.GetByteCount(frame),
            MaxResponseBytes = body.Length,
            RequestTimeoutSeconds = 5
        }, TestContext.Current.CancellationToken);

        for (var index = 0; index < 10; index++)
        {
            await transport.SendAsync("{}", TestContext.Current.CancellationToken);
            (await transport.ReceiveAsync(TestContext.Current.CancellationToken)).ShouldBe(frame);
        }

        peer.Requests.Count.ShouldBe(10);
        foreach (var request in peer.Requests)
            request.Body.ShouldBe("{}");
    }
}
