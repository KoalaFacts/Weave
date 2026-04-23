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

/// <summary>
/// Covers branches in <see cref="ToolRegistryGrain"/> that the original
/// tests don't reach: access configuration, tool resolution (with access
/// gating + reconnect-on-stale), connect-time failure path, and agent-tool
/// grants. Uses the same substitute-wired fixture as <c>ToolRegistryGrainTests</c>.
/// </summary>
public sealed class ToolRegistryGrainBranchTests
{
    private static IPersistentState<ToolRegistryState> CreatePersistentState()
    {
        var persistentState = Substitute.For<IPersistentState<ToolRegistryState>>();
        persistentState.State.Returns(new ToolRegistryState());
        persistentState.ReadStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync().Returns(Task.CompletedTask);
        return persistentState;
    }

    private sealed class Fixture
    {
        public IGrainFactory GrainFactory { get; } = Substitute.For<IGrainFactory>();
        public IToolGrain ToolGrain { get; } = Substitute.For<IToolGrain>();
        public ISecretProxyGrain SecretProxy { get; } = Substitute.For<ISecretProxyGrain>();
        public ILifecycleManager Lifecycle { get; } = Substitute.For<ILifecycleManager>();
        public IEventBus EventBus { get; } = Substitute.For<IEventBus>();
        public IPersistentState<ToolRegistryState> State { get; } = CreatePersistentState();

        public ToolRegistryGrain Grain { get; }

        public Fixture(bool failConnect = false)
        {
            var tokenService = new CapabilityTokenService(
                Microsoft.Extensions.Options.Options.Create(
                    new CapabilityTokenOptions { SigningKey = "test-signing-key-that-is-at-least-32-chars-long" }),
                TimeProvider.System);

            if (failConnect)
            {
                ToolGrain.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>())
                    .Returns(Task.FromException<ToolHandle>(new InvalidOperationException("tool unreachable")));
            }
            else
            {
                ToolGrain.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>())
                    .Returns(ci => Task.FromResult(new ToolHandle
                    {
                        ToolName = ci.Arg<ToolSpec>().Name,
                        Type = ci.Arg<ToolSpec>().Type,
                        IsConnected = true
                    }));
            }

            ToolGrain.GetHandleAsync().Returns(Task.FromResult<ToolHandle?>(new ToolHandle
            {
                ToolName = "x",
                Type = ToolType.Cli,
                IsConnected = true
            }));
            ToolGrain.GetSchemaAsync().Returns(Task.FromResult(new ToolSchema { ToolName = "x", Description = "d" }));
            SecretProxy.SubstituteAsync(Arg.Any<string>()).Returns(ci => ci.Arg<string>());

            GrainFactory.GetGrain<IToolGrain>(Arg.Any<string>(), null).Returns(ToolGrain);
            GrainFactory.GetGrain<ISecretProxyGrain>(Arg.Any<string>(), null).Returns(SecretProxy);

            Grain = new ToolRegistryGrain(
                GrainFactory, tokenService, Lifecycle, EventBus, TimeProvider.System,
                Substitute.For<ILogger<ToolRegistryGrain>>(), State);
        }
    }

    private static Dictionary<string, ToolDefinition> SingleTool(string name = "shell") => new()
    {
        [name] = new ToolDefinition { Type = "cli", Cli = new CliConfig { Shell = "/bin/bash", AllowedCommands = ["ls"] } }
    };

    [Fact]
    public async Task ConfigureAccessAsync_StoresUniqueToolNamesPerAgent()
    {
        var fx = new Fixture();
        var access = new Dictionary<string, List<string>>
        {
            ["researcher"] = ["search", "search", "shell"], // duplicate "search" — should be de-duped
            ["coder"] = ["shell"]
        };

        await fx.Grain.ConfigureAccessAsync(access);

        fx.State.State.AgentToolAccess["researcher"].ShouldBe(["search", "shell"]);
        fx.State.State.AgentToolAccess["coder"].ShouldBe(["shell"]);
    }

    [Fact]
    public async Task GrantAgentToolsAsync_DeduplicatesAndReplaces()
    {
        var fx = new Fixture();

        await fx.Grain.GrantAgentToolsAsync("agent-a", ["t1", "t1", "t2"]);
        await fx.Grain.GrantAgentToolsAsync("agent-a", ["t3"]);

        fx.State.State.AgentToolAccess["agent-a"].ShouldBe(["t3"], "a subsequent grant replaces, not appends");
    }

    [Fact]
    public async Task ResolveAsync_AgentNotInAccessTable_ReturnsNull()
    {
        var fx = new Fixture();
        await fx.Grain.ConnectToolsAsync(SingleTool());

        var result = await fx.Grain.ResolveAsync(agentName: "unauthorized", toolName: "shell");

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_ToolNotInAgentAccessList_ReturnsNull()
    {
        var fx = new Fixture();
        await fx.Grain.ConnectToolsAsync(SingleTool());
        await fx.Grain.GrantAgentToolsAsync("agent-a", ["different-tool"]);

        var result = await fx.Grain.ResolveAsync("agent-a", "shell");

        result.ShouldBeNull("agent granted different-tool only, so shell must be denied");
    }

    [Fact]
    public async Task ResolveAsync_ToolDefinitionMissing_ReturnsNull()
    {
        var fx = new Fixture();
        await fx.Grain.GrantAgentToolsAsync("agent-a", ["ghost-tool"]);

        var result = await fx.Grain.ResolveAsync("agent-a", "ghost-tool");

        result.ShouldBeNull("no matching tool definition → cannot resolve");
    }

    [Fact]
    public async Task ResolveAsync_AllowedConnectedTool_ReturnsResolutionWithToken()
    {
        var fx = new Fixture();
        await fx.Grain.ConnectToolsAsync(SingleTool());
        await fx.Grain.GrantAgentToolsAsync("agent-a", ["shell"]);

        var result = await fx.Grain.ResolveAsync("agent-a", "shell");

        result.ShouldNotBeNull();
        result!.ToolName.ShouldBe("shell");
        result.Token.Grants.ShouldContain("tool:shell");
        result.Schema.ToolName.ShouldBe("x");
    }

    [Fact]
    public async Task ResolveAsync_ToolHandleStale_ReconnectsBeforeReturning()
    {
        var fx = new Fixture();
        await fx.Grain.ConnectToolsAsync(SingleTool());
        await fx.Grain.GrantAgentToolsAsync("agent-a", ["shell"]);
        fx.ToolGrain.ClearReceivedCalls();
        // Simulate a stale handle: grain reports null, so ResolveAsync must re-call ConnectAsync.
        fx.ToolGrain.GetHandleAsync().Returns(Task.FromResult<ToolHandle?>(null));

        var result = await fx.Grain.ResolveAsync("agent-a", "shell");

        result.ShouldNotBeNull();
        await fx.ToolGrain.Received().ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>());
    }

    [Fact]
    public async Task ConnectToolsAsync_ConnectAsyncThrows_RecordsErrorStateAndPublishesErrorEvent()
    {
        var fx = new Fixture(failConnect: true);

        await Should.ThrowAsync<InvalidOperationException>(
            () => fx.Grain.ConnectToolsAsync(SingleTool()));

        var connection = await fx.Grain.GetConnectionAsync("shell");
        connection.ShouldNotBeNull();
        connection!.Status.ShouldBe(ToolConnectionStatus.Error);
        connection.ErrorMessage.ShouldNotBeNull();
        connection.ErrorMessage.ShouldContain("tool unreachable");
        await fx.EventBus.Received().PublishAsync(Arg.Any<Events.ToolErrorEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_ConnectionInErrorState_ReconnectsBeforeReturningToken()
    {
        // Simulates: an earlier connect attempt failed and left Status=Error;
        // a later resolve should attempt to reconnect (connectIfNotConnected branch).
        var fx = new Fixture();
        // Seed state manually in Error status.
        fx.State.State.Definitions["shell"] = new ToolDefinition
        {
            Type = "cli",
            Cli = new CliConfig { Shell = "/bin/bash", AllowedCommands = ["ls"] }
        };
        fx.State.State.Connections["shell"] = new ToolConnection
        {
            ToolName = "shell",
            ToolType = "cli",
            Status = ToolConnectionStatus.Error
        };
        await fx.Grain.GrantAgentToolsAsync("agent-a", ["shell"]);
        fx.ToolGrain.ClearReceivedCalls();

        var result = await fx.Grain.ResolveAsync("agent-a", "shell");

        result.ShouldNotBeNull();
        await fx.ToolGrain.Received().ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>());
    }
}
