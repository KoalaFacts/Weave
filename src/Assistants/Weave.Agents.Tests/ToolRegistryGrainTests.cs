using Microsoft.Extensions.Logging;
using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Security.Grains;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Lifecycle;
using Weave.Tools.Grains;
using Weave.Tools.Models;
using Weave.Workspaces.Models;

namespace Weave.Agents.Tests;

public sealed class ToolRegistryGrainTests
{
    private static IPersistentState<ToolRegistryState> CreatePersistentState()
    {
        var persistentState = Substitute.For<IPersistentState<ToolRegistryState>>();
        persistentState.State.Returns(new ToolRegistryState());
        persistentState.ReadStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync().Returns(Task.CompletedTask);
        persistentState.ClearStateAsync().Returns(Task.CompletedTask);
        return persistentState;
    }

    private static (ToolRegistryGrain Grain, ILifecycleManager Lifecycle, IEventBus EventBus) CreateGrain()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var toolGrain = Substitute.For<IToolGrain>();
        var secretProxy = Substitute.For<ISecretProxyGrain>();
        var lifecycle = Substitute.For<ILifecycleManager>();
        var eventBus = Substitute.For<IEventBus>();
        var logger = Substitute.For<ILogger<ToolRegistryGrain>>();
        var tokenService = new CapabilityTokenService(
            Microsoft.Extensions.Options.Options.Create(
                new CapabilityTokenOptions { SigningKey = "test-signing-key-that-is-at-least-32-chars-long" }));
        var persistentState = CreatePersistentState();

        toolGrain.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>())
            .Returns(callInfo => Task.FromResult(new ToolHandle
            {
                ToolName = callInfo.Arg<ToolSpec>().Name,
                Type = callInfo.Arg<ToolSpec>().Type,
                IsConnected = true
            }));
        toolGrain.GetHandleAsync().Returns(Task.FromResult<ToolHandle?>(new ToolHandle
        {
            ToolName = "connected",
            Type = ToolType.Cli,
            IsConnected = true
        }));
        toolGrain.GetSchemaAsync().Returns(Task.FromResult(new ToolSchema
        {
            ToolName = "connected",
            Description = "Connected tool"
        }));
        secretProxy.SubstituteAsync(Arg.Any<string>()).Returns(callInfo => callInfo.Arg<string>());

        grainFactory.GetGrain<IToolGrain>(Arg.Any<string>(), null).Returns(toolGrain);
        grainFactory.GetGrain<ISecretProxyGrain>(Arg.Any<string>(), null).Returns(secretProxy);

        var grain = new ToolRegistryGrain(grainFactory, tokenService, lifecycle, eventBus, logger, persistentState);
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
