using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Security.Tokens;
using Weave.Tools.Connectors;
using Weave.Tools.Tool;
using Weave.Workspaces.Manifest;
namespace Weave.Tools.Tests;

public sealed class McpToolConnectorTests
{
    private static readonly CapabilityToken _token = new()
    {
        TokenId = "test-token",
        WorkspaceId = "ws-1",
        Grants = ["tool:test"]
    };

    private static ToolSpec NewSpec() => new()
    {
        Name = "test-mcp",
        Type = ToolType.Mcp,
        Mcp = new McpConfig { Server = "stub", Args = [], Env = [] }
    };

    [Fact]
    public async Task ConnectAsync_NullMcpConfig_Throws()
    {
        var (connector, _) = NewConnector();
        var spec = new ToolSpec { Name = "bad", Type = ToolType.Mcp };

        await Should.ThrowAsync<InvalidOperationException>(() => connector.ConnectAsync(spec, _token));
    }

    [Fact]
    public async Task ConnectAsync_PerformsInitializeHandshakeThenSendsInitializedNotification()
    {
        var transport = new StubMcpTransport();
        var (connector, _) = NewConnector(transport);

        var connectTask = connector.ConnectAsync(NewSpec(), _token, TestContext.Current.CancellationToken);

        var initRequest = await transport.ReadClientFrameAsync();
        using (var initDoc = JsonDocument.Parse(initRequest))
        {
            initDoc.RootElement.GetProperty("method").GetString().ShouldBe("initialize");
            initDoc.RootElement.GetProperty("params").GetProperty("protocolVersion").GetString().ShouldBe("2024-11-05");
            initDoc.RootElement.GetProperty("params").GetProperty("clientInfo").GetProperty("name").GetString().ShouldBe("weave");

            var id = initDoc.RootElement.GetProperty("id").GetInt64();
            await transport.WriteServerFrameAsync(Reply(id, """{"protocolVersion":"2024-11-05","serverInfo":{"name":"stub","version":"1.0"}}"""));
        }

        var initialized = await transport.ReadClientFrameAsync();
        using (var notifDoc = JsonDocument.Parse(initialized))
        {
            notifDoc.RootElement.GetProperty("method").GetString().ShouldBe("notifications/initialized");
            notifDoc.RootElement.TryGetProperty("id", out _).ShouldBeFalse();
        }

        var handle = await connectTask;
        handle.IsConnected.ShouldBeTrue();
        handle.Type.ShouldBe(ToolType.Mcp);

        await connector.DisconnectAsync(handle, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ConnectAsync_InitializeError_DisposesTransport()
    {
        var transport = new StubMcpTransport();
        var (connector, _) = NewConnector(transport);

        var connectTask = connector.ConnectAsync(NewSpec(), _token, TestContext.Current.CancellationToken);

        var initRequest = await transport.ReadClientFrameAsync();
        using var doc = JsonDocument.Parse(initRequest);
        var id = doc.RootElement.GetProperty("id").GetInt64();
        await transport.WriteServerFrameAsync(ReplyError(id, -32601, "protocol mismatch"));

        await Should.ThrowAsync<InvalidOperationException>(() => connectTask);
        transport.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task DiscoverSchemaAsync_SendsToolsListAndReturnsMenu()
    {
        var (connector, transport) = await ConnectAsync();

        var schemaTask = connector.DiscoverSchemaAsync(new ToolHandle
        {
            ToolName = "test-mcp",
            Type = ToolType.Mcp,
            ConnectionId = transport.HandleId!,
            IsConnected = true
        }, TestContext.Current.CancellationToken);

        var listRequest = await transport.ReadClientFrameAsync();
        using (var doc = JsonDocument.Parse(listRequest))
        {
            doc.RootElement.GetProperty("method").GetString().ShouldBe("tools/list");
            var id = doc.RootElement.GetProperty("id").GetInt64();
            await transport.WriteServerFrameAsync(Reply(id, """{"tools":[{"name":"search","description":"Search the web","inputSchema":{"type":"object","properties":{"q":{"type":"string"}},"required":["q"]}},{"name":"echo","description":"Echo text"}]}"""));
        }

        var schema = await schemaTask;
        schema.ToolName.ShouldBe("test-mcp");
        schema.Description.ShouldContain("search");
        schema.Description.ShouldContain("Search the web");
        schema.Description.ShouldContain("echo");
        schema.Parameters.Count.ShouldBe(1);
        schema.Parameters[0].Name.ShouldBe("method");
        schema.Parameters[0].Required.ShouldBeTrue();
        schema.Parameters[0].Description.ShouldContain("search");

        await connector.DisconnectAsync(new ToolHandle { ConnectionId = transport.HandleId!, ToolName = "test-mcp", Type = ToolType.Mcp }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DiscoverSchemaAsync_CachesToolsList_SecondCallDoesNotResendRequest()
    {
        var (connector, transport) = await ConnectAsync();
        var handle = new ToolHandle { ToolName = "test-mcp", Type = ToolType.Mcp, ConnectionId = transport.HandleId!, IsConnected = true };

        var firstTask = connector.DiscoverSchemaAsync(handle, TestContext.Current.CancellationToken);
        var listRequest = await transport.ReadClientFrameAsync();
        using (var doc = JsonDocument.Parse(listRequest))
        {
            var id = doc.RootElement.GetProperty("id").GetInt64();
            await transport.WriteServerFrameAsync(Reply(id, """{"tools":[{"name":"x"}]}"""));
        }
        await firstTask;

        var secondSchema = await connector.DiscoverSchemaAsync(handle, TestContext.Current.CancellationToken);
        secondSchema.Description.ShouldContain("x");

        transport.HasPendingClientFrame.ShouldBeFalse();

        await connector.DisconnectAsync(handle, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task InvokeAsync_SendsToolsCallWithArgumentsAndReturnsTextContent()
    {
        var (connector, transport) = await ConnectAsync();
        var handle = new ToolHandle { ToolName = "test-mcp", Type = ToolType.Mcp, ConnectionId = transport.HandleId!, IsConnected = true };

        var invocation = new ToolInvocation
        {
            ToolName = "test-mcp",
            Method = "search",
            Parameters = new() { ["q"] = "weave", ["limit"] = "5" }
        };

        var invokeTask = connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        var callRequest = await transport.ReadClientFrameAsync();
        using (var doc = JsonDocument.Parse(callRequest))
        {
            doc.RootElement.GetProperty("method").GetString().ShouldBe("tools/call");
            doc.RootElement.GetProperty("params").GetProperty("name").GetString().ShouldBe("search");
            var args = doc.RootElement.GetProperty("params").GetProperty("arguments");
            args.GetProperty("q").GetString().ShouldBe("weave");
            args.GetProperty("limit").GetInt32().ShouldBe(5);

            var id = doc.RootElement.GetProperty("id").GetInt64();
            await transport.WriteServerFrameAsync(Reply(id, """{"content":[{"type":"text","text":"hit1"},{"type":"text","text":"hit2"}],"isError":false}"""));
        }

        var result = await invokeTask;
        result.Success.ShouldBeTrue();
        result.Output.ShouldBe("hit1\nhit2");

        await connector.DisconnectAsync(handle, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task InvokeAsync_IsErrorTrue_ReturnsFailureWithErrorText()
    {
        var (connector, transport) = await ConnectAsync();
        var handle = new ToolHandle { ToolName = "test-mcp", Type = ToolType.Mcp, ConnectionId = transport.HandleId!, IsConnected = true };

        var invokeTask = connector.InvokeAsync(handle, new ToolInvocation { ToolName = "test-mcp", Method = "boom" }, TestContext.Current.CancellationToken);

        var callRequest = await transport.ReadClientFrameAsync();
        using (var doc = JsonDocument.Parse(callRequest))
        {
            var id = doc.RootElement.GetProperty("id").GetInt64();
            await transport.WriteServerFrameAsync(Reply(id, """{"content":[{"type":"text","text":"server explosion"}],"isError":true}"""));
        }

        var result = await invokeTask;
        result.Success.ShouldBeFalse();
        result.Error.ShouldBe("server explosion");

        await connector.DisconnectAsync(handle, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task InvokeAsync_JsonRpcError_ReturnsFailureWithErrorMessage()
    {
        var (connector, transport) = await ConnectAsync();
        var handle = new ToolHandle { ToolName = "test-mcp", Type = ToolType.Mcp, ConnectionId = transport.HandleId!, IsConnected = true };

        var invokeTask = connector.InvokeAsync(handle, new ToolInvocation { ToolName = "test-mcp", Method = "missing" }, TestContext.Current.CancellationToken);

        var callRequest = await transport.ReadClientFrameAsync();
        using (var doc = JsonDocument.Parse(callRequest))
        {
            var id = doc.RootElement.GetProperty("id").GetInt64();
            await transport.WriteServerFrameAsync(ReplyError(id, -32601, "Unknown tool"));
        }

        var result = await invokeTask;
        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("Unknown tool");

        await connector.DisconnectAsync(handle, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task InvokeAsync_NotConnected_ReturnsFailure()
    {
        var (connector, _) = NewConnector();
        var handle = new ToolHandle { ToolName = "test", Type = ToolType.Mcp, ConnectionId = "nope", IsConnected = true };

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "test", Method = "x" },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("not connected");
    }

    [Fact]
    public async Task InvokeAsync_TransportClosedMidFlight_PendingRequestFailsCleanly()
    {
        var (connector, transport) = await ConnectAsync();
        var handle = new ToolHandle { ToolName = "test-mcp", Type = ToolType.Mcp, ConnectionId = transport.HandleId!, IsConnected = true };

        var invokeTask = connector.InvokeAsync(handle, new ToolInvocation { ToolName = "test-mcp", Method = "x" }, TestContext.Current.CancellationToken);

        await transport.ReadClientFrameAsync();
        transport.CloseServerSide();

        var result = await invokeTask;
        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("closed");

        await connector.DisconnectAsync(handle, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DisconnectAsync_DisposesTransport()
    {
        var (connector, transport) = await ConnectAsync();
        var handle = new ToolHandle { ToolName = "test-mcp", Type = ToolType.Mcp, ConnectionId = transport.HandleId!, IsConnected = true };

        await connector.DisconnectAsync(handle, TestContext.Current.CancellationToken);

        transport.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task DisconnectAsync_UnknownConnectionId_DoesNotThrow()
    {
        var (connector, _) = NewConnector();
        var handle = new ToolHandle { ToolName = "test", Type = ToolType.Mcp, ConnectionId = "unknown", IsConnected = true };

        await connector.DisconnectAsync(handle, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void ToolType_IsMcp()
    {
        NewConnector().connector.ToolType.ShouldBe(ToolType.Mcp);
    }

    private static string Reply(long id, string resultJson) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":" + resultJson + "}";

    private static string ReplyError(long id, int code, string message) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"error\":{\"code\":" + code + ",\"message\":\"" + message + "\"}}";

    private static (McpToolConnector connector, StubMcpTransport transport) NewConnector(StubMcpTransport? transport = null)
    {
        var captured = transport ?? new StubMcpTransport();
        var connector = new McpToolConnector((_, _) => Task.FromResult<IMcpTransport>(captured), NullLogger<McpToolConnector>.Instance);
        return (connector, captured);
    }

    private static async Task<(McpToolConnector connector, StubMcpTransport transport)> ConnectAsync()
    {
        var (connector, transport) = NewConnector();
        var connectTask = connector.ConnectAsync(NewSpec(), _token, TestContext.Current.CancellationToken);

        var initRequest = await transport.ReadClientFrameAsync();
        using (var doc = JsonDocument.Parse(initRequest))
        {
            var id = doc.RootElement.GetProperty("id").GetInt64();
            await transport.WriteServerFrameAsync(Reply(id, """{"protocolVersion":"2024-11-05","serverInfo":{"name":"stub"}}"""));
        }

        await transport.ReadClientFrameAsync();

        var handle = await connectTask;
        transport.HandleId = handle.ConnectionId;
        return (connector, transport);
    }
}

internal sealed class StubMcpTransport : IMcpTransport
{
    private readonly Channel<string> _clientToServer = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _serverToClient = Channel.CreateUnbounded<string>();

    public bool Disposed { get; private set; }
    public bool HasExited => Disposed;
    public int? ExitCode => Disposed ? 0 : null;
    public bool HasPendingClientFrame => _clientToServer.Reader.Count > 0;
    public string? HandleId { get; set; }

    public Task SendAsync(string json, CancellationToken ct) =>
        _clientToServer.Writer.WriteAsync(json, ct).AsTask();

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        try
        { return await _serverToClient.Reader.ReadAsync(ct); }
        catch (ChannelClosedException) { return null; }
    }

    public Task<string> ReadClientFrameAsync() => _clientToServer.Reader.ReadAsync().AsTask();

    public Task WriteServerFrameAsync(string json) => _serverToClient.Writer.WriteAsync(json).AsTask();

    public void CloseServerSide() => _serverToClient.Writer.TryComplete();

    public string FormatDiagnosticTail() => "stub transport";

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _serverToClient.Writer.TryComplete();
        _clientToServer.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
