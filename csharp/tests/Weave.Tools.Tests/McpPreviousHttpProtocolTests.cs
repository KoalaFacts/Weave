using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Tools.Connectors;
using Weave.Workspaces.Manifest;

namespace Weave.Tools.Tests;

public sealed class McpPreviousHttpProtocolTests
{
    [Fact]
    public async Task InitializeAsync_UnsupportedNegotiatedRevision_RejectsPeer()
    {
        McpHttpTestPeer? peer = null;
        peer = new McpHttpTestPeer(async (stream, ct) =>
        {
            using var request = JsonDocument.Parse(peer!.Requests.Last().Body);
            if (request.RootElement.GetProperty("method").GetString() == "server/discover")
            {
                await McpHttpTestPeer.ReplyAsync(stream, ct, status: 400);
                return;
            }
            var id = request.RootElement.GetProperty("id").GetInt64();
            await McpHttpTestPeer.ReplyAsync(stream, ct,
                $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"protocolVersion\":\"2025-06-18\"}}}}");
        });
        await using var ownedPeer = peer;
        await using var connection = new McpConnection(await HttpMcpTransport.ConnectAsync(new McpConfig
        {
            Url = peer.Endpoint,
            AllowPrivateEndpoints = true
        }, TestContext.Current.CancellationToken), "echo", NullLogger.Instance);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            connection.InitializeAsync(TestContext.Current.CancellationToken));
        peer.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task InitializeAsync_PreviousRevisionWithSession_SendsNegotiatedHeaders()
    {
        McpHttpTestPeer? peer = null;
        peer = new McpHttpTestPeer(async (stream, ct) =>
        {
            var request = peer!.Requests.Last();
            using var document = JsonDocument.Parse(request.Body);
            var root = document.RootElement;
            var method = root.GetProperty("method").GetString();
            if (method == "server/discover")
            {
                await McpHttpTestPeer.ReplyAsync(stream, ct, status: 400);
                return;
            }
            if (method == "initialize")
            {
                root.GetProperty("params").GetProperty("protocolVersion").GetString().ShouldBe("2025-11-25");
                request.Headers.ShouldNotContain("Mcp-Session-Id:", Case.Insensitive);
                var id = root.GetProperty("id").GetInt64();
                await McpHttpTestPeer.ReplyAsync(stream, ct,
                    $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"protocolVersion\":\"2025-11-25\",\"serverInfo\":{{\"name\":\"previous\",\"version\":\"1\"}}}}}}",
                    "Mcp-Session-Id: session-1\r\n");
                return;
            }
            request.Headers.ShouldContain("MCP-Protocol-Version: 2025-11-25", Case.Insensitive);
            request.Headers.ShouldContain("Mcp-Session-Id: session-1", Case.Insensitive);
            if (method == "notifications/initialized")
            {
                await McpHttpTestPeer.ReplyAsync(stream, ct, status: 202);
                return;
            }
            method.ShouldBe("tools/list");
            var listId = root.GetProperty("id").GetInt64();
            await McpHttpTestPeer.ReplyAsync(stream, ct,
                $"{{\"jsonrpc\":\"2.0\",\"id\":{listId},\"result\":{{\"tools\":[]}}}}");
        });
        await using var ownedPeer = peer;
        await using var connection = new McpConnection(await HttpMcpTransport.ConnectAsync(new McpConfig
        {
            Url = peer.Endpoint,
            AllowPrivateEndpoints = true
        }, TestContext.Current.CancellationToken), "echo", NullLogger.Instance);

        await connection.InitializeAsync(TestContext.Current.CancellationToken);
        (await connection.ListToolsAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
        connection.ProtocolVersion.ShouldBe("2025-11-25");
    }

    [Fact]
    public async Task ListToolsAsync_ExpiredSession_DoesNotRetryRequest()
    {
        McpHttpTestPeer? peer = null;
        peer = new McpHttpTestPeer(async (stream, ct) =>
        {
            using var request = JsonDocument.Parse(peer!.Requests.Last().Body);
            var method = request.RootElement.GetProperty("method").GetString();
            if (method == "server/discover")
                await McpHttpTestPeer.ReplyAsync(stream, ct, status: 400);
            else if (method == "initialize")
            {
                var id = request.RootElement.GetProperty("id").GetInt64();
                await McpHttpTestPeer.ReplyAsync(stream, ct,
                    $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"protocolVersion\":\"2025-11-25\"}}}}",
                    "Mcp-Session-Id: session-2\r\n");
            }
            else if (method == "notifications/initialized")
                await McpHttpTestPeer.ReplyAsync(stream, ct, status: 202);
            else
                await McpHttpTestPeer.ReplyAsync(stream, ct, status: 404);
        });
        await using var ownedPeer = peer;
        await using var connection = new McpConnection(await HttpMcpTransport.ConnectAsync(new McpConfig
        {
            Url = peer.Endpoint,
            AllowPrivateEndpoints = true
        }, TestContext.Current.CancellationToken), "echo", NullLogger.Instance);

        await connection.InitializeAsync(TestContext.Current.CancellationToken);
        await Should.ThrowAsync<IOException>(() => connection.ListToolsAsync(TestContext.Current.CancellationToken));
        await Should.ThrowAsync<IOException>(() => connection.ListToolsAsync(TestContext.Current.CancellationToken));
        peer.Requests.Count.ShouldBe(4);
    }

    [Fact]
    public async Task CallToolAsync_PreviousRevisionTaskResult_FailsClosed()
    {
        McpHttpTestPeer? peer = null;
        peer = new McpHttpTestPeer(async (stream, ct) =>
        {
            using var request = JsonDocument.Parse(peer!.Requests.Last().Body);
            var method = request.RootElement.GetProperty("method").GetString();
            if (method == "server/discover")
            {
                await McpHttpTestPeer.ReplyAsync(stream, ct, status: 400);
                return;
            }
            if (method == "notifications/initialized")
            {
                await McpHttpTestPeer.ReplyAsync(stream, ct, status: 202);
                return;
            }
            var id = request.RootElement.GetProperty("id").GetInt64();
            var result = method switch
            {
                "initialize" => """{"protocolVersion":"2025-11-25"}""",
                "tools/call" => """{"task":{"taskId":"deferred"}}""",
                _ => throw new InvalidOperationException($"Unexpected request {method}.")
            };
            await McpHttpTestPeer.ReplyAsync(stream, ct,
                $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{result}}}");
        });
        await using var ownedPeer = peer;
        await using var connection = new McpConnection(await HttpMcpTransport.ConnectAsync(new McpConfig
        {
            Url = peer.Endpoint,
            AllowPrivateEndpoints = true
        }, TestContext.Current.CancellationToken), "echo", NullLogger.Instance);

        await connection.InitializeAsync(TestContext.Current.CancellationToken);
        await Should.ThrowAsync<InvalidOperationException>(() =>
            connection.CallToolAsync("echo", new JsonObject(), TestContext.Current.CancellationToken));
        peer.Requests.Count.ShouldBe(4);
    }
}
