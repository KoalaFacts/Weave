using Microsoft.Extensions.Logging;
using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Shared.Events;
using Weave.Shared.Lifecycle;
using Weave.Workspaces.Models;

namespace Weave.Agents.Tests;

public sealed class ToolRegistryGrainTests
{
    private static (ToolRegistryGrain Grain, ILifecycleManager Lifecycle, IEventBus EventBus) CreateGrain()
    {
        var lifecycle = Substitute.For<ILifecycleManager>();
        var eventBus = Substitute.For<IEventBus>();
        var logger = Substitute.For<ILogger<ToolRegistryGrain>>();
        var grain = new ToolRegistryGrain(lifecycle, eventBus, logger);
        return (grain, lifecycle, eventBus);
    }

    private static Dictionary<string, ToolDefinition> CreateTools() => new()
    {
        ["code-search"] = new ToolDefinition
        {
            Type = "mcp",
            Mcp = new McpConfig { Server = "npx", Args = ["-y", "@anthropic/code-search-mcp"] }
        },
        ["shell"] = new ToolDefinition
        {
            Type = "cli",
            Cli = new CliConfig { Shell = "/bin/bash", AllowedCommands = ["ls", "cat", "grep"] }
        }
    };

    [Fact]
    public async Task ConnectToolsAsync_ConnectsAllTools()
    {
        var (grain, _, _) = CreateGrain();
        var tools = CreateTools();

        await grain.ConnectToolsAsync(tools);

        var connections = await grain.GetAllConnectionsAsync();
        connections.Count.ShouldBe(2);
        connections.ShouldAllBe(c => c.Status == ToolConnectionStatus.Connected);
    }

    [Fact]
    public async Task ConnectToolsAsync_SetsCorrectEndpoints()
    {
        var (grain, _, _) = CreateGrain();
        var tools = CreateTools();

        await grain.ConnectToolsAsync(tools);

        var mcpConn = await grain.GetConnectionAsync("code-search");
        mcpConn!.Endpoint.ShouldBe("npx");
        mcpConn.ToolType.ShouldBe("mcp");

        var cliConn = await grain.GetConnectionAsync("shell");
        cliConn!.Endpoint.ShouldBeNull();
        cliConn.ToolType.ShouldBe("cli");
    }

    [Fact]
    public async Task ConnectToolsAsync_RunsLifecycleHooks()
    {
        var (grain, lifecycle, _) = CreateGrain();

        await grain.ConnectToolsAsync(CreateTools());

        await lifecycle.Received(2).RunHooksAsync(
            LifecyclePhase.ToolConnecting,
            Arg.Any<LifecycleContext>(),
            Arg.Any<CancellationToken>());
        await lifecycle.Received(2).RunHooksAsync(
            LifecyclePhase.ToolConnected,
            Arg.Any<LifecycleContext>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConnectToolsAsync_PublishesEvents()
    {
        var (grain, _, eventBus) = CreateGrain();

        await grain.ConnectToolsAsync(CreateTools());

        await eventBus.Received(2).PublishAsync(
            Arg.Any<Events.ToolConnectedEvent>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetConnectionAsync_UnknownTool_ReturnsNull()
    {
        var (grain, _, _) = CreateGrain();

        var result = await grain.GetConnectionAsync("nonexistent");

        result.ShouldBeNull();
    }

    [Fact]
    public async Task DisconnectAllAsync_ClearsAllConnections()
    {
        var (grain, _, _) = CreateGrain();
        await grain.ConnectToolsAsync(CreateTools());

        await grain.DisconnectAllAsync();

        var connections = await grain.GetAllConnectionsAsync();
        connections.ShouldBeEmpty();
    }

    [Fact]
    public async Task DisconnectAllAsync_RunsLifecycleHooks()
    {
        var (grain, lifecycle, _) = CreateGrain();
        await grain.ConnectToolsAsync(CreateTools());

        await grain.DisconnectAllAsync();

        await lifecycle.Received(2).RunHooksAsync(
            LifecyclePhase.ToolDisconnecting,
            Arg.Any<LifecycleContext>(),
            Arg.Any<CancellationToken>());
        await lifecycle.Received(2).RunHooksAsync(
            LifecyclePhase.ToolDisconnected,
            Arg.Any<LifecycleContext>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisconnectAllAsync_PublishesEvents()
    {
        var (grain, _, eventBus) = CreateGrain();
        await grain.ConnectToolsAsync(CreateTools());

        await grain.DisconnectAllAsync();

        await eventBus.Received(2).PublishAsync(
            Arg.Any<Events.ToolDisconnectedEvent>(),
            Arg.Any<CancellationToken>());
    }
}
