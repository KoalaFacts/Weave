using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Tools.Connectors;
using Weave.Workspaces.Manifest;

namespace Weave.Tools.Tests;

public sealed class McpModernHttpProtocolTests
{
    [Fact]
    public void ForTool_NestedAnnotatedPrimitives_ProducesMatchingHeaders()
    {
        using var document = JsonDocument.Parse("""{"type":"object","properties":{"context":{"type":"object","properties":{"region":{"type":"string","x-mcp-header":"Region"}}},"count":{"type":"integer","x-mcp-header":"Count"},"enabled":{"type":"boolean","x-mcp-header":"Enabled"},"absent":{"type":"string","x-mcp-header":"Absent"}}}""");
        var tool = new McpTool { Name = "echo", InputSchema = document.RootElement.Clone() };
        var arguments = JsonNode.Parse("""{"context":{"region":"west"},"count":42,"enabled":false,"absent":null}""")!;

        var headers = McpHttpHeaderValue.ForTool(tool, arguments);

        headers["Region"].ShouldBe("west");
        headers["Count"].ShouldBe("42");
        headers["Enabled"].ShouldBe("false");
        headers.ShouldNotContainKey("Absent");
    }

    [Theory]
    [InlineData("1.0", "1")]
    [InlineData("1e2", "100")]
    [InlineData("1.20e1", "12")]
    [InlineData("10e-1", "1")]
    [InlineData("0e999999999999999999999", "0")]
    [InlineData("9007199254740991.0", "9007199254740991")]
    public void ForTool_IntegralJsonNumber_UsesNormalizedHeaderValue(string raw, string expected)
    {
        using var document = JsonDocument.Parse("""{"type":"object","properties":{"count":{"type":"integer","x-mcp-header":"Count"}}}""");
        var tool = new McpTool { Name = "echo", InputSchema = document.RootElement.Clone() };
        var arguments = JsonNode.Parse($"{{\"count\":{raw}}}")!;

        McpHttpHeaderValue.ForTool(tool, arguments)["Count"].ShouldBe(expected);
    }

    [Theory]
    [InlineData("1.00000000000000000000000000001")]
    [InlineData("1.5")]
    [InlineData("9007199254740992")]
    public void ForTool_InvalidInteger_RejectsHeaderValue(string raw)
    {
        using var document = JsonDocument.Parse("""{"type":"object","properties":{"count":{"type":"integer","x-mcp-header":"Count"}}}""");
        var tool = new McpTool { Name = "echo", InputSchema = document.RootElement.Clone() };

        Should.Throw<InvalidOperationException>(() => McpHttpHeaderValue.ForTool(tool,
            JsonNode.Parse($"{{\"count\":{raw}}}")!));
    }

    [Fact]
    public void HasValidSchema_InstanceExamplesContainingAnnotationName_RemainsAvailable()
    {
        using var document = JsonDocument.Parse("""{"type":"object","properties":{"region":{"type":"string","x-mcp-header":"Region"},"data":{"type":"object","default":{"x-mcp-header":"example"},"examples":[{"x-mcp-header":"example"}],"const":{"x-mcp-header":"example"}}}}""");
        var tool = new McpTool { Name = "echo", InputSchema = document.RootElement.Clone() };

        McpHttpHeaderValue.HasValidSchema(tool).ShouldBeTrue();
        McpHttpHeaderValue.ForTool(tool, JsonNode.Parse("""{"region":"west"}""")!)["Region"].ShouldBe("west");
    }

    [Theory]
    [InlineData("""{"type":"object","properties":{"one":{"type":"string","x-mcp-header":"Region"},"two":{"type":"string","x-mcp-header":"region"}}}""")]
    [InlineData("""{"type":"object","properties":{"one":{"type":"number","x-mcp-header":"Region"}}}""")]
    [InlineData("""{"type":"object","items":{"type":"string","x-mcp-header":"Region"}}""")]
    public void ForTool_InvalidHeaderAnnotations_RejectsDefinition(string schema)
    {
        using var document = JsonDocument.Parse(schema);
        var tool = new McpTool { Name = "echo", InputSchema = document.RootElement.Clone() };

        McpHttpHeaderValue.HasValidSchema(tool).ShouldBeFalse();
    }

    [Fact]
    public async Task ListToolsAsync_InvalidHeaderDefinition_OmitsOnlyThatTool()
    {
        McpHttpTestPeer? peer = null;
        peer = new McpHttpTestPeer(async (stream, ct) =>
        {
            using var request = JsonDocument.Parse(peer!.Requests.Last().Body);
            var root = request.RootElement;
            var id = root.GetProperty("id").GetInt64();
            var result = root.GetProperty("method").GetString() switch
            {
                "server/discover" => """{"resultType":"complete","supportedVersions":["2026-07-28"]}""",
                "tools/list" => """{"resultType":"complete","tools":[{"name":"invalid","inputSchema":{"type":"object","properties":{"region":{"type":"number","x-mcp-header":"Region"}}}},{"name":"valid","inputSchema":{"type":"object"}}]}""",
                _ => throw new InvalidOperationException("Unexpected MCP request.")
            };
            await McpHttpTestPeer.ReplyAsync(stream, ct, $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{result}}}");
        });
        await using var ownedPeer = peer;
        await using var connection = new McpConnection(await HttpMcpTransport.ConnectAsync(new McpConfig
        {
            Url = peer.Endpoint,
            AllowPrivateEndpoints = true
        }, TestContext.Current.CancellationToken), "echo", NullLogger.Instance);

        await connection.InitializeAsync(TestContext.Current.CancellationToken);
        (await connection.ListToolsAsync(TestContext.Current.CancellationToken))
            .Select(tool => tool.Name).ShouldBe(["valid"]);
    }

    [Fact]
    public async Task ListToolsAsync_ChangedModernServerIdentity_RejectsResponse()
    {
        McpHttpTestPeer? peer = null;
        peer = new McpHttpTestPeer(async (stream, ct) =>
        {
            using var request = JsonDocument.Parse(peer!.Requests.Last().Body);
            var root = request.RootElement;
            var id = root.GetProperty("id").GetInt64();
            var result = root.GetProperty("method").GetString() switch
            {
                "server/discover" => """{"resultType":"complete","supportedVersions":["2026-07-28"],"_meta":{"io.modelcontextprotocol/serverInfo":{"name":"first","version":"1"}}}""",
                "tools/list" => """{"resultType":"complete","tools":[],"_meta":{"io.modelcontextprotocol/serverInfo":{"name":"other","version":"1"}}}""",
                _ => throw new InvalidOperationException("Unexpected MCP request.")
            };
            await McpHttpTestPeer.ReplyAsync(stream, ct, $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{result}}}");
        });
        await using var ownedPeer = peer;
        await using var connection = new McpConnection(await HttpMcpTransport.ConnectAsync(new McpConfig
        {
            Url = peer.Endpoint,
            AllowPrivateEndpoints = true
        }, TestContext.Current.CancellationToken), "echo", NullLogger.Instance);

        await connection.InitializeAsync(TestContext.Current.CancellationToken);
        await Should.ThrowAsync<InvalidOperationException>(() =>
            connection.ListToolsAsync(TestContext.Current.CancellationToken));
        peer.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task CallToolAsync_InputRequiredResult_DoesNotRetryEffect()
    {
        McpHttpTestPeer? peer = null;
        peer = new McpHttpTestPeer(async (stream, ct) =>
        {
            using var request = JsonDocument.Parse(peer!.Requests.Last().Body);
            var root = request.RootElement;
            var method = root.GetProperty("method").GetString();
            var id = root.GetProperty("id").GetInt64();
            var result = method switch
            {
                "server/discover" => """{"resultType":"complete","supportedVersions":["2026-07-28"]}""",
                "tools/list" => """{"resultType":"complete","tools":[{"name":"echo","inputSchema":{"type":"object"}}]}""",
                "tools/call" => """{"resultType":"input_required","inputRequests":[],"requestState":"opaque"}""",
                _ => throw new InvalidOperationException($"Unexpected method {method}.")
            };
            await McpHttpTestPeer.ReplyAsync(stream, ct, $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{result}}}");
        });
        await using var ownedPeer = peer;
        await using var connection = new McpConnection(await HttpMcpTransport.ConnectAsync(new McpConfig
        {
            Url = peer.Endpoint,
            AllowPrivateEndpoints = true
        }, TestContext.Current.CancellationToken), "echo", NullLogger.Instance);

        await connection.InitializeAsync(TestContext.Current.CancellationToken);
        await connection.ListToolsAsync(TestContext.Current.CancellationToken);
        await Should.ThrowAsync<InvalidOperationException>(() =>
            connection.CallToolAsync("echo", new JsonObject(), TestContext.Current.CancellationToken));
        peer.Requests.Count(item => JsonDocument.Parse(item.Body).RootElement
            .GetProperty("method").GetString() == "tools/call").ShouldBe(1);
    }

    [Fact]
    public async Task InitializeAsync_LegacyBadRequest_FallsBackWithoutCallingTool()
    {
        McpHttpTestPeer? peer = null;
        peer = new McpHttpTestPeer(async (stream, ct) =>
        {
            using var request = JsonDocument.Parse(peer!.Requests.Last().Body);
            var root = request.RootElement;
            var method = root.GetProperty("method").GetString();
            if (method == "notifications/initialized")
            {
                await McpHttpTestPeer.ReplyAsync(stream, ct, status: 202);
                return;
            }
            var id = root.GetProperty("id").GetInt64();
            if (method == "server/discover")
                await McpHttpTestPeer.ReplyAsync(stream, ct, status: 400);
            else if (method == "initialize")
                await McpHttpTestPeer.ReplyAsync(stream, ct,
                    $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"protocolVersion\":\"2024-11-05\",\"serverInfo\":{{\"name\":\"legacy\",\"version\":\"1\"}}}}}}");
            else
                throw new InvalidOperationException($"Unexpected method {method}.");
        });
        await using var ownedPeer = peer;
        await using var connection = new McpConnection(await HttpMcpTransport.ConnectAsync(new McpConfig
        {
            Url = peer.Endpoint,
            AllowPrivateEndpoints = true
        }, TestContext.Current.CancellationToken), "echo", NullLogger.Instance);

        await connection.InitializeAsync(TestContext.Current.CancellationToken);
        connection.ServerName.ShouldBe("legacy");
        peer.Requests.Select(item => JsonDocument.Parse(item.Body).RootElement.GetProperty("method").GetString())
            .ShouldBe(["server/discover", "initialize", "notifications/initialized"]);
    }

    [Fact]
    public async Task InitializeAsync_RecognizedModernProtocolError_DoesNotDowngrade()
    {
        McpHttpTestPeer? peer = null;
        peer = new McpHttpTestPeer(async (stream, ct) =>
        {
            using var request = JsonDocument.Parse(peer!.Requests.Last().Body);
            request.RootElement.GetProperty("method").GetString().ShouldBe("server/discover");
            var id = request.RootElement.GetProperty("id").GetInt64();
            await McpHttpTestPeer.ReplyAsync(stream, ct,
                $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"error\":{{\"code\":-32022,\"message\":\"Unsupported version\"}}}}", status: 400);
        });
        await using var ownedPeer = peer;
        await using var connection = new McpConnection(await HttpMcpTransport.ConnectAsync(new McpConfig
        {
            Url = peer.Endpoint,
            AllowPrivateEndpoints = true
        }, TestContext.Current.CancellationToken), "echo", NullLogger.Instance);

        await Should.ThrowAsync<McpHttpStatusException>(() =>
            connection.InitializeAsync(TestContext.Current.CancellationToken));
        peer.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task InitializeAsync_ModernOnlyPeer_UsesStatelessProtocolForListAndCall()
    {
        McpHttpTestPeer? peer = null;
        peer = new McpHttpTestPeer(async (stream, ct) =>
        {
            var request = peer!.Requests.Last();
            using var document = JsonDocument.Parse(request.Body);
            var root = document.RootElement;
            var method = root.GetProperty("method").GetString();
            if (method == "initialize")
            {
                await McpHttpTestPeer.ReplyAsync(stream, ct, status: 400);
                return;
            }

            request.Headers.ShouldContain("MCP-Protocol-Version: 2026-07-28", Case.Insensitive);
            request.Headers.ShouldContain($"Mcp-Method: {method}", Case.Insensitive);
            var metadata = root.GetProperty("params").GetProperty("_meta");
            metadata.GetProperty("io.modelcontextprotocol/protocolVersion").GetString().ShouldBe("2026-07-28");
            metadata.GetProperty("io.modelcontextprotocol/clientCapabilities").ValueKind.ShouldBe(JsonValueKind.Object);
            metadata.GetProperty("io.modelcontextprotocol/clientInfo").GetProperty("name").GetString().ShouldBe("weave");

            var result = method switch
            {
                "server/discover" => """{"resultType":"complete","supportedVersions":["2026-07-28"],"_meta":{"io.modelcontextprotocol/serverInfo":{"name":"modern-echo","version":"1.0"}}}""",
                "tools/list" => """{"resultType":"complete","tools":[{"name":"echo","inputSchema":{"type":"object","properties":{"text":{"type":"string","x-mcp-header":"Text"}}}}],"_meta":{"io.modelcontextprotocol/serverInfo":{"name":"modern-echo","version":"1.0"}}}""",
                "tools/call" => """{"resultType":"complete","content":[{"type":"text","text":"hello"}],"_meta":{"io.modelcontextprotocol/serverInfo":{"name":"modern-echo","version":"1.0"}}}""",
                _ => throw new InvalidOperationException($"Unexpected request {method}.")
            };
            if (method == "tools/call")
            {
                request.Headers.ShouldContain("Mcp-Name: echo", Case.Insensitive);
                request.Headers.ShouldContain("Mcp-Param-Text: =?base64?IGhlbGxvIOS4lueVjCA=?=", Case.Insensitive);
                root.GetProperty("params").GetProperty("name").GetString().ShouldBe("echo");
            }
            var id = root.GetProperty("id").GetInt64();
            await McpHttpTestPeer.ReplyAsync(stream, ct, $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{result}}}");
        });
        await using var ownedPeer = peer;

        await using var connection = new McpConnection(await HttpMcpTransport.ConnectAsync(new McpConfig
        {
            Url = peer.Endpoint,
            AllowPrivateEndpoints = true
        }, TestContext.Current.CancellationToken), "echo", NullLogger.Instance);

        await connection.InitializeAsync(TestContext.Current.CancellationToken);
        connection.ServerName.ShouldBe("modern-echo");
        connection.ServerVersion.ShouldBe("1.0");
        (await connection.ListToolsAsync(TestContext.Current.CancellationToken)).Single().Name.ShouldBe("echo");
        var result = await connection.CallToolAsync("echo", new JsonObject { ["text"] = " hello 世界 " },
            TestContext.Current.CancellationToken);
        result.Content.Single().Text.ShouldBe("hello");
        peer.Requests.Select(item => JsonDocument.Parse(item.Body).RootElement.GetProperty("method").GetString())
            .ShouldBe(["server/discover", "tools/list", "tools/call"]);
    }
}
