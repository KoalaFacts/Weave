using Microsoft.Extensions.Logging.Abstractions;
using Weave.Security.Tokens;
using Weave.Tools.Connectors;
using Weave.Tools.Tool;
using Weave.Workspaces.Manifest;

namespace Weave.Tools.Tests;

[Trait("Category", "Integration")]
public sealed class OpenApiTransportFailureTests
{
    private const string RequestBody = """{"name":"inventory-widget"}""";

    [Fact]
    public async Task InvokeAsync_CompleteResponse_ReturnsSuccessfulBody()
    {
        await using var peer = new McpHttpTestPeer((stream, ct) =>
            McpHttpTestPeer.ReplyAsync(stream, ct, """{"id":42}"""));
        using var client = new HttpClient();
        var connector = new OpenApiToolConnector(client, NullLogger<OpenApiToolConnector>.Instance);
        var handle = await ConnectAsync(connector, peer);

        var result = await connector.InvokeAsync(handle, Invocation(), TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.ToolName.ShouldBe("inventory-api");
        result.Output.ShouldBe("""{"id":42}""");
        result.Error.ShouldBeNull();
        result.Duration.ShouldBeGreaterThan(TimeSpan.Zero);
        AssertSinglePost(peer);
    }

    [Fact]
    public async Task InvokeAsync_ResponseBodyTruncated_ReturnsFailureWithoutResending()
    {
        await using var peer = new McpHttpTestPeer(async (stream, ct) =>
        {
            await McpHttpTestPeer.HeadersAsync(stream, ct, length: 100);
            await stream.WriteAsync("{\"id\":"u8.ToArray(), ct);
        });
        using var client = new HttpClient();
        var connector = new OpenApiToolConnector(client, NullLogger<OpenApiToolConnector>.Instance);
        var handle = await ConnectAsync(connector, peer);

        var result = await connector.InvokeAsync(handle, Invocation(), TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.ToolName.ShouldBe("inventory-api");
        result.Output.ShouldBeEmpty();
        result.Error.ShouldNotBeNullOrWhiteSpace();
        result.Duration.ShouldBeGreaterThan(TimeSpan.Zero);
        AssertSinglePost(peer);
    }

    [Fact]
    public async Task InvokeAsync_CallerCancelsAfterResponseHeaders_ReturnsFailureWithoutResending()
    {
        var headersSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var peer = new McpHttpTestPeer(async (stream, ct) =>
        {
            await McpHttpTestPeer.HeadersAsync(stream, ct);
            headersSent.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var connector = new OpenApiToolConnector(client, NullLogger<OpenApiToolConnector>.Instance);
        var handle = await ConnectAsync(connector, peer);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var invoking = connector.InvokeAsync(handle, Invocation(), caller.Token);
        await headersSent.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        invoking.IsCompleted.ShouldBeFalse();

        await caller.CancelAsync();
        var result = await invoking.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.ToolName.ShouldBe("inventory-api");
        result.Output.ShouldBeEmpty();
        result.Error.ShouldNotBeNullOrWhiteSpace();
        result.Duration.ShouldBeGreaterThan(TimeSpan.Zero);
        AssertSinglePost(peer);
    }

    private static ToolInvocation Invocation() => new()
    {
        ToolName = "inventory-api",
        Method = "createItem",
        RawInput = RequestBody
    };

    private static void AssertSinglePost(McpHttpTestPeer peer)
    {
        var request = peer.Requests.ShouldHaveSingleItem();
        request.Headers.ShouldStartWith("POST /mcp HTTP/1.1\r\n");
        request.Body.ShouldBe(RequestBody);
    }

    private static async Task<ToolHandle> ConnectAsync(OpenApiToolConnector connector, McpHttpTestPeer peer)
    {
        var authority = new Uri(peer.Endpoint).GetLeftPart(UriPartial.Authority);
        var spec = $$$"""
            {
              "openapi": "3.0.0",
              "servers": [{"url": "{{{authority}}}"}],
              "paths": {
                "/mcp": {
                  "post": {"operationId": "createItem", "requestBody": {"required": true}}
                }
              }
            }
            """;
        await using var specPeer = new McpHttpTestPeer((stream, ct) => McpHttpTestPeer.ReplyAsync(stream, ct, spec));
        return await connector.ConnectAsync(new ToolSpec
        {
            Name = "inventory-api",
            Type = ToolType.OpenApi,
            OpenApi = new OpenApiConfig { SpecUrl = specPeer.Endpoint }
        }, new CapabilityToken
        {
            TokenId = "test-token",
            WorkspaceId = "ws-1",
            Grants = ["tool:inventory-api"]
        }, TestContext.Current.CancellationToken);
    }
}
