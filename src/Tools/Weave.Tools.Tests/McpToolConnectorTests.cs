using System.Collections.Concurrent;
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

    // --- ConnectAsync with real process ---

    [Fact]
    public async Task ConnectAsync_WithArgsAndEnv_ReturnsHandle()
    {
        var connector = CreateConnector();
        var spec = new ToolSpec
        {
            Name = "dotnet-ver",
            Type = ToolType.Mcp,
            Mcp = new McpConfig
            {
                Server = "dotnet",
                Args = ["--version"],
                Env = new Dictionary<string, string> { ["WEAVE_TEST_VAR"] = "1" }
            }
        };

        var handle = await connector.ConnectAsync(spec, _testToken, TestContext.Current.CancellationToken);

        handle.IsConnected.ShouldBeTrue();
        handle.ToolName.ShouldBe("dotnet-ver");
        handle.Type.ShouldBe(ToolType.Mcp);
        handle.ConnectionId.ShouldNotBeNullOrEmpty();

        // Clean up
        await connector.DisconnectAsync(handle, TestContext.Current.CancellationToken);
    }

    // --- DisconnectAsync with connected process ---

    [Fact]
    public async Task DisconnectAsync_ConnectedProcess_CleansUp()
    {
        var connector = CreateConnector();
        var spec = new ToolSpec
        {
            Name = "dotnet-ver",
            Type = ToolType.Mcp,
            Mcp = new McpConfig
            {
                Server = "dotnet",
                Args = ["--version"],
                Env = new Dictionary<string, string>()
            }
        };

        var handle = await connector.ConnectAsync(spec, _testToken, TestContext.Current.CancellationToken);
        await connector.DisconnectAsync(handle, TestContext.Current.CancellationToken);

        // Second disconnect is a no-op (connection already removed)
        await connector.DisconnectAsync(handle, TestContext.Current.CancellationToken);
    }

    // --- InvokeAsync with exited process ---

    [Fact]
    public async Task InvokeAsync_ProcessExited_ReturnsFailureWithExitCode()
    {
        var connector = CreateConnector();
        var spec = new ToolSpec
        {
            Name = "dotnet-ver",
            Type = ToolType.Mcp,
            Mcp = new McpConfig
            {
                Server = "dotnet",
                Args = ["--version"],
                Env = new Dictionary<string, string>()
            }
        };

        var handle = await connector.ConnectAsync(spec, _testToken, TestContext.Current.CancellationToken);

        // Wait for dotnet --version to finish
        await Task.Delay(2000, TestContext.Current.CancellationToken);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "dotnet-ver", Method = "test", Parameters = [] },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("exited");

        await connector.DisconnectAsync(handle, TestContext.Current.CancellationToken);
    }

    // --- FormatStderrTail ---

    [Fact]
    public void FormatStderrTail_EmptyQueue_ReturnsEmpty()
    {
        var queue = new ConcurrentQueue<string>();

        McpToolConnector.FormatStderrTail(queue).ShouldBeEmpty();
    }

    [Fact]
    public void FormatStderrTail_PopulatedQueue_ReturnsFormattedTail()
    {
        var queue = new ConcurrentQueue<string>();
        queue.Enqueue("line1");
        queue.Enqueue("line2");
        queue.Enqueue("line3");

        var result = McpToolConnector.FormatStderrTail(queue);

        result.ShouldStartWith("stderr tail:");
        result.ShouldContain("line1");
        result.ShouldContain("line3");
    }

    [Fact]
    public void FormatStderrTail_MoreThanFiveLines_ShowsOnlyLastFive()
    {
        var queue = new ConcurrentQueue<string>();
        for (var i = 1; i <= 8; i++)
            queue.Enqueue($"line{i}");

        var result = McpToolConnector.FormatStderrTail(queue);

        result.ShouldNotContain("line1");
        result.ShouldNotContain("line2");
        result.ShouldNotContain("line3");
        result.ShouldContain("line4");
        result.ShouldContain("line8");
    }
}
