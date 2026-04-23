using Microsoft.Extensions.Logging.Abstractions;
using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Shared.Ids;
using Weave.Workspaces.Models;

namespace Weave.Agents.Tests;

/// <summary>
/// Exercises the real <see cref="AgentSupervisorGrain"/> directly — not
/// via <c>Substitute.For&lt;IAgentSupervisorGrain&gt;()</c> which covers
/// only the interface contract. This pattern mirrors <see cref="AgentGrainTests"/>:
/// inject a stub <see cref="IPersistentState{T}"/>, a real
/// <see cref="IGrainFactory"/> substitute, and assert on state + delegated calls.
/// </summary>
public sealed class AgentSupervisorGrainTests
{
    private static readonly WorkspaceId TestWorkspaceId = WorkspaceId.From("ws-1");

    private static IPersistentState<AgentSupervisorState> CreatePersistentState(AgentSupervisorState? initial = null)
    {
        var state = initial ?? new AgentSupervisorState();
        var persistentState = Substitute.For<IPersistentState<AgentSupervisorState>>();
        persistentState.State.Returns(state);
        persistentState.ReadStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync().Returns(Task.CompletedTask);
        return persistentState;
    }

    private static (AgentSupervisorGrain Grain, IGrainFactory Factory, IPersistentState<AgentSupervisorState> State) CreateGrain(
        AgentSupervisorState? initial = null)
    {
        var factory = Substitute.For<IGrainFactory>();
        var state = CreatePersistentState(initial);
        var grain = new AgentSupervisorGrain(factory, NullLogger<AgentSupervisorGrain>.Instance, state);
        // AgentSupervisorGrain calls GetPrimaryKeyString() internally; direct
        // instantiation throws NullReferenceException which the grain catches
        // and falls back to persistentState.State.WorkspaceId or "unknown-workspace".
        // We pre-seed the state so EnsureWorkspaceId resolves deterministically.
        state.State.WorkspaceId = TestWorkspaceId.ToString();
        return (grain, factory, state);
    }

    private static WorkspaceManifest CreateManifest(params (string name, string[] tools)[] agents)
    {
        var agentMap = new Dictionary<string, AgentDefinition>();
        var toolMap = new Dictionary<string, ToolDefinition>();
        foreach (var (name, tools) in agents)
        {
            agentMap[name] = new AgentDefinition { Model = "test-model", Tools = [.. tools] };
            foreach (var tool in tools)
                toolMap[tool] = new ToolDefinition { Type = "mcp" };
        }
        return new WorkspaceManifest { Version = "1.0", Name = "ws-1", Agents = agentMap, Tools = toolMap };
    }

    [Fact]
    public async Task ActivateAllAsync_NoAgents_WritesEmptyStateAndDoesNothing()
    {
        var (grain, factory, state) = CreateGrain();
        var manifest = CreateManifest();

        await grain.ActivateAllAsync(manifest);

        state.State.AgentNames.ShouldBeEmpty();
        factory.DidNotReceive().GetGrain<IAgentGrain>(Arg.Any<string>());
    }

    [Fact]
    public async Task ActivateAllAsync_TwoAgents_ActivatesEachAndStoresNames()
    {
        var (grain, factory, state) = CreateGrain();
        var researcher = Substitute.For<IAgentGrain>();
        var coder = Substitute.For<IAgentGrain>();
        var tools = Substitute.For<IToolRegistryGrain>();

        factory.GetGrain<IAgentGrain>("ws-1/researcher", null).Returns(researcher);
        factory.GetGrain<IAgentGrain>("ws-1/coder", null).Returns(coder);
        factory.GetGrain<IToolRegistryGrain>("ws-1", null).Returns(tools);
        tools.GetConnectionAsync(Arg.Any<string>()).Returns((ToolConnection?)null);

        await grain.ActivateAllAsync(CreateManifest(("researcher", []), ("coder", [])));

        state.State.AgentNames.ShouldBe(["researcher", "coder"]);
        await researcher.Received(1).ActivateAgentAsync(TestWorkspaceId, Arg.Any<AgentDefinition>());
        await coder.Received(1).ActivateAgentAsync(TestWorkspaceId, Arg.Any<AgentDefinition>());
    }

    [Fact]
    public async Task ActivateAllAsync_ConnectsOnlyToolsReportingConnectedStatus()
    {
        var (grain, factory, _) = CreateGrain();
        var agent = Substitute.For<IAgentGrain>();
        var tools = Substitute.For<IToolRegistryGrain>();

        factory.GetGrain<IAgentGrain>("ws-1/researcher", null).Returns(agent);
        factory.GetGrain<IToolRegistryGrain>("ws-1", null).Returns(tools);
        tools.GetConnectionAsync("search").Returns(new ToolConnection { ToolName = "search", ToolType = "mcp", Status = ToolConnectionStatus.Connected });
        tools.GetConnectionAsync("offline").Returns(new ToolConnection { ToolName = "offline", ToolType = "mcp", Status = ToolConnectionStatus.Error });

        await grain.ActivateAllAsync(CreateManifest(("researcher", ["search", "offline"])));

        await agent.Received(1).ConnectToolAsync("search");
        await agent.DidNotReceive().ConnectToolAsync("offline");
    }

    [Fact]
    public async Task ActivateAllAsync_WhenAgentActivationThrows_ExceptionPropagates()
    {
        var (grain, factory, _) = CreateGrain();
        var agent = Substitute.For<IAgentGrain>();
        factory.GetGrain<IAgentGrain>("ws-1/researcher", null).Returns(agent);
        agent.ActivateAgentAsync(Arg.Any<WorkspaceId>(), Arg.Any<AgentDefinition>())
            .Returns(Task.FromException<AgentState>(new InvalidOperationException("activation failed")));

        await Should.ThrowAsync<InvalidOperationException>(
            () => grain.ActivateAllAsync(CreateManifest(("researcher", []))));
    }

    [Fact]
    public async Task DeactivateAllAsync_CallsDeactivateOnEachStoredAgent()
    {
        var (grain, factory, state) = CreateGrain(new AgentSupervisorState
        {
            AgentNames = ["researcher", "coder"],
            WorkspaceId = TestWorkspaceId.ToString()
        });
        var researcher = Substitute.For<IAgentGrain>();
        var coder = Substitute.For<IAgentGrain>();
        factory.GetGrain<IAgentGrain>("ws-1/researcher", null).Returns(researcher);
        factory.GetGrain<IAgentGrain>("ws-1/coder", null).Returns(coder);

        await grain.DeactivateAllAsync();

        await researcher.Received(1).DeactivateAsync();
        await coder.Received(1).DeactivateAsync();
        state.State.AgentNames.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeactivateAllAsync_WhenOneAgentThrows_OtherAgentsStillDeactivated()
    {
        var (grain, factory, state) = CreateGrain(new AgentSupervisorState
        {
            AgentNames = ["broken", "survivor"],
            WorkspaceId = TestWorkspaceId.ToString()
        });
        var broken = Substitute.For<IAgentGrain>();
        var survivor = Substitute.For<IAgentGrain>();
        broken.DeactivateAsync().Returns(Task.FromException(new InvalidOperationException("boom")));
        factory.GetGrain<IAgentGrain>("ws-1/broken", null).Returns(broken);
        factory.GetGrain<IAgentGrain>("ws-1/survivor", null).Returns(survivor);

        await grain.DeactivateAllAsync();

        await survivor.Received(1).DeactivateAsync();
        state.State.AgentNames.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetAllAgentStatesAsync_FetchesStateFromEachAgentGrain()
    {
        var (grain, factory, _) = CreateGrain(new AgentSupervisorState
        {
            AgentNames = ["a", "b"],
            WorkspaceId = TestWorkspaceId.ToString()
        });
        var aGrain = Substitute.For<IAgentGrain>();
        var bGrain = Substitute.For<IAgentGrain>();
        aGrain.GetStateAsync().Returns(new AgentState { AgentId = "ws-1/a", WorkspaceId = TestWorkspaceId, AgentName = "a" });
        bGrain.GetStateAsync().Returns(new AgentState { AgentId = "ws-1/b", WorkspaceId = TestWorkspaceId, AgentName = "b" });
        factory.GetGrain<IAgentGrain>("ws-1/a", null).Returns(aGrain);
        factory.GetGrain<IAgentGrain>("ws-1/b", null).Returns(bGrain);

        var states = await grain.GetAllAgentStatesAsync();

        states.Count.ShouldBe(2);
        states.ShouldContain(s => s.AgentName == "a");
        states.ShouldContain(s => s.AgentName == "b");
    }

    [Fact]
    public async Task GetAgentStateAsync_UnknownAgent_ReturnsNullWithoutGrainLookup()
    {
        var (grain, factory, _) = CreateGrain(new AgentSupervisorState
        {
            AgentNames = ["known"],
            WorkspaceId = TestWorkspaceId.ToString()
        });

        var state = await grain.GetAgentStateAsync("nope");

        state.ShouldBeNull();
        factory.DidNotReceive().GetGrain<IAgentGrain>("ws-1/nope", null);
    }

    [Fact]
    public async Task GetAgentStateAsync_KnownAgent_ReturnsGrainState()
    {
        var (grain, factory, _) = CreateGrain(new AgentSupervisorState
        {
            AgentNames = ["known"],
            WorkspaceId = TestWorkspaceId.ToString()
        });
        var known = Substitute.For<IAgentGrain>();
        known.GetStateAsync().Returns(new AgentState
        {
            AgentId = "ws-1/known",
            WorkspaceId = TestWorkspaceId,
            AgentName = "known",
            Status = AgentStatus.Active
        });
        factory.GetGrain<IAgentGrain>("ws-1/known", null).Returns(known);

        var state = await grain.GetAgentStateAsync("known");

        state.ShouldNotBeNull();
        state.Status.ShouldBe(AgentStatus.Active);
    }
}
