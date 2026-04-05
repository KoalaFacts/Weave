using Microsoft.Extensions.Logging;
using Weave.Security.Tokens;
using Weave.Tools.Connectors;
using Weave.Tools.Models;
using Weave.Workspaces.Models;

namespace Weave.Tools.Tests;

public sealed class McpToolConnectorTests
{
    private static readonly CapabilityToken _testToken = new()
    {
        TokenId = "test-token",
        WorkspaceId = "ws-1",
        Grants = ["tool:mcp-tool"]
    };

    private static McpToolConnector CreateConnector() =>
        new(Substitute.For<ILogger<McpToolConnector>>());

    // --- ConnectAsync ---

    [Fact]
    public async Task ConnectAsync_NullMcpConfig_Throws()
    {
        var connector = CreateConnector();
        var spec = new ToolSpec { Name = "bad", Type = ToolType.Mcp };

        await Should.ThrowAsync<InvalidOperationException>(
            () => connector.ConnectAsync(spec, _testToken));
    }

    [Fact]
    public async Task ConnectAsync_NonexistentServer_Throws()
    {
        var connector = CreateConnector();
        var spec = new ToolSpec
        {
            Name = "bad",
            Type = ToolType.Mcp,
            Mcp = new McpConfig
            {
                Server = "this-binary-does-not-exist-weave-test",
                Args = [],
                Env = new Dictionary<string, string>()
            }
        };

        // Process.Start throws Win32Exception for nonexistent binaries
        await Should.ThrowAsync<Exception>(
            () => connector.ConnectAsync(spec, _testToken));
    }

    // --- DisconnectAsync ---

    [Fact]
    public async Task DisconnectAsync_UnknownConnectionId_DoesNotThrow()
    {
        var connector = CreateConnector();
        var handle = new ToolHandle
        {
            ToolName = "test",
            Type = ToolType.Mcp,
            ConnectionId = "nonexistent-id",
            IsConnected = true
        };

        // Should not throw even for unknown connection
        await connector.DisconnectAsync(handle, TestContext.Current.CancellationToken);
    }

    // --- InvokeAsync ---

    [Fact]
    public async Task InvokeAsync_NotConnected_ReturnsFailure()
    {
        var connector = CreateConnector();
        var handle = new ToolHandle
        {
            ToolName = "test",
            Type = ToolType.Mcp,
            ConnectionId = "nonexistent",
            IsConnected = true
        };
        var invocation = new ToolInvocation { ToolName = "test", Method = "run", Parameters = [] };

        var result = await connector.InvokeAsync(handle, invocation, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("not connected");
        result.ToolName.ShouldBe("test");
    }

    // --- DiscoverSchemaAsync ---

    [Fact]
    public async Task DiscoverSchemaAsync_ReturnsDescription()
    {
        var connector = CreateConnector();
        var handle = new ToolHandle
        {
            ToolName = "my-mcp",
            Type = ToolType.Mcp,
            ConnectionId = "some-id",
            IsConnected = true
        };

        var schema = await connector.DiscoverSchemaAsync(handle, TestContext.Current.CancellationToken);

        schema.ToolName.ShouldBe("my-mcp");
        schema.Description.ShouldContain("my-mcp");
    }

    [Fact]
    public void ToolType_IsMcp()
    {
        CreateConnector().ToolType.ShouldBe(ToolType.Mcp);
    }
}
