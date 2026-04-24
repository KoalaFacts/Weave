using Microsoft.Extensions.Logging;
using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Security.Actors;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Lifecycle;
using Weave.Tools.Actors;
using Weave.Tools.Models;
using Weave.Workspaces.Models;

namespace Weave.Agents.Tests;

public sealed class ToolRegistryActorTests
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

    private static (ToolRegistryActor Actor, ILifecycleManager Lifecycle, IEventBus EventBus) CreateActor()
    {
        var actorFactory = Substitute.For<IActorFactory>();
        var toolActor = Substitute.For<IToolActor>();
        var secretProxy = Substitute.For<ISecretProxyActor>();
        var lifecycle = Substitute.For<ILifecycleManager>();
        var eventBus = Substitute.For<IEventBus>();
        var logger = Substitute.For<ILogger<ToolRegistryActor>>();
        var tokenService = new CapabilityTokenService(
            Microsoft.Extensions.Options.Options.Create(
                new CapabilityTokenOptions { SigningKey = "test-signing-key-that-is-at-least-32-chars-long" }),
            TimeProvider.System);
        var persistentState = CreatePersistentState();

        toolActor.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>())
            .Returns(callInfo => Task.FromResult(new ToolHandle
            {
                ToolName = callInfo.Arg<ToolSpec>().Name,
                Type = callInfo.Arg<ToolSpec>().Type,
                IsConnected = true
            }));
        toolActor.GetHandleAsync().Returns(Task.FromResult<ToolHandle?>(new ToolHandle
        {
            ToolName = "connected",
            Type = ToolType.Cli,
            IsConnected = true
        }));
        toolActor.GetSchemaAsync().Returns(Task.FromResult(new ToolSchema
        {
            ToolName = "connected",
            Description = "Connected tool"
        }));
        secretProxy.SubstituteAsync(Arg.Any<string>()).Returns(callInfo => callInfo.Arg<string>());

        actorFactory.GetActor<IToolActor>(Arg.Any<string>(), null).Returns(toolActor);
        actorFactory.GetActor<ISecretProxyActor>(Arg.Any<string>(), null).Returns(secretProxy);

        var actor = new ToolRegistryActor(new TestVirtualActorProvider(actorFactory), tokenService, lifecycle, eventBus, TimeProvider.System, logger, persistentState);
        return (actor, lifecycle, eventBus);
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
        var (actor, _, _) = CreateActor();
        var tools = CreateTools();

        await actor.ConnectToolsAsync(tools);

        var connections = await actor.GetAllConnectionsAsync();
        connections.Count.ShouldBe(2);
        connections.ShouldAllBe(c => c.Status == ToolConnectionStatus.Connected);
    }

    [Fact]
    public async Task ConnectToolsAsync_SetsCorrectEndpoints()
    {
        var (actor, _, _) = CreateActor();
        var tools = CreateTools();

        await actor.ConnectToolsAsync(tools);

        var mcpConn = await actor.GetConnectionAsync("code-search");
        mcpConn!.Endpoint.ShouldBe("npx");
        mcpConn.ToolType.ShouldBe("mcp");

        var cliConn = await actor.GetConnectionAsync("shell");
        cliConn!.Endpoint.ShouldBeNull();
        cliConn.ToolType.ShouldBe("cli");
    }

    [Fact]
    public async Task ConnectToolsAsync_RunsLifecycleHooks()
    {
        var (actor, lifecycle, _) = CreateActor();

        await actor.ConnectToolsAsync(CreateTools());

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
        var (actor, _, eventBus) = CreateActor();

        await actor.ConnectToolsAsync(CreateTools());

        await eventBus.Received(2).PublishAsync(
            Arg.Any<Events.ToolConnectedEvent>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetConnectionAsync_UnknownTool_ReturnsNull()
    {
        var (actor, _, _) = CreateActor();

        var result = await actor.GetConnectionAsync("nonexistent");

        result.ShouldBeNull();
    }

    [Fact]
    public async Task DisconnectAllAsync_ClearsAllConnections()
    {
        var (actor, _, _) = CreateActor();
        await actor.ConnectToolsAsync(CreateTools());

        await actor.DisconnectAllAsync();

        var connections = await actor.GetAllConnectionsAsync();
        connections.ShouldBeEmpty();
    }

    [Fact]
    public async Task DisconnectAllAsync_RunsLifecycleHooks()
    {
        var (actor, lifecycle, _) = CreateActor();
        await actor.ConnectToolsAsync(CreateTools());

        await actor.DisconnectAllAsync();

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
        var (actor, _, eventBus) = CreateActor();
        await actor.ConnectToolsAsync(CreateTools());

        await actor.DisconnectAllAsync();

        await eventBus.Received(2).PublishAsync(
            Arg.Any<Events.ToolDisconnectedEvent>(),
            Arg.Any<CancellationToken>());
    }
}
