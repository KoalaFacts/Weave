using Microsoft.Extensions.Logging.Abstractions;
using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Shared.Ids;
using Weave.Workspaces.Models;

namespace Weave.Agents.Tests;

/// <summary>
/// Exercises the real <see cref="AgentSupervisorActor"/> directly — not
/// via <c>Substitute.For&lt;IAgentSupervisorActor&gt;()</c> which covers
/// only the interface contract. This pattern mirrors <see cref="AgentActorTests"/>:
/// inject a stub <see cref="IPersistentState{T}"/>, a real
/// <see cref="IActorFactory"/> substitute, and assert on state + delegated calls.
/// </summary>
public sealed class AgentSupervisorActorTests
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

    private static (AgentSupervisorActor Actor, IActorFactory Factory, IPersistentState<AgentSupervisorState> State) CreateActor(
        AgentSupervisorState? initial = null)
    {
        var factory = Substitute.For<IActorFactory>();
        var state = CreatePersistentState(initial);
        var actor = new AgentSupervisorActor(new TestVirtualActorProvider(factory), NullLogger<AgentSupervisorActor>.Instance, state);
        // AgentSupervisorActor calls GetPrimaryKeyString() internally; direct
        // instantiation throws NullReferenceException which the actor catches
        // and falls back to persistentState.State.WorkspaceId or "unknown-workspace".
        // We pre-seed the state so EnsureWorkspaceId resolves deterministically.
        state.State.WorkspaceId = TestWorkspaceId.ToString();
        return (actor, factory, state);
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
        var (actor, factory, state) = CreateActor();
        var manifest = CreateManifest();

        await actor.ActivateAllAsync(manifest);

        state.State.AgentNames.ShouldBeEmpty();
        factory.DidNotReceive().GetGrain<IAgentActor>(Arg.Any<string>());
    }

    [Fact]
    public async Task ActivateAllAsync_TwoAgents_ActivatesEachAndStoresNames()
    {
        var (actor, factory, state) = CreateActor();
        var researcher = Substitute.For<IAgentActor>();
        var coder = Substitute.For<IAgentActor>();
        var tools = Substitute.For<IToolRegistryActor>();

        factory.GetGrain<IAgentActor>("ws-1/researcher", null).Returns(researcher);
        factory.GetGrain<IAgentActor>("ws-1/coder", null).Returns(coder);
        factory.GetGrain<IToolRegistryActor>("ws-1", null).Returns(tools);
        tools.GetConnectionAsync(Arg.Any<string>()).Returns((ToolConnection?)null);

        await actor.ActivateAllAsync(CreateManifest(("researcher", []), ("coder", [])));

        state.State.AgentNames.ShouldBe(["researcher", "coder"]);
        await researcher.Received(1).ActivateAgentAsync(TestWorkspaceId, Arg.Any<AgentDefinition>());
        await coder.Received(1).ActivateAgentAsync(TestWorkspaceId, Arg.Any<AgentDefinition>());
    }

    [Fact]
    public async Task ActivateAllAsync_ConnectsOnlyToolsReportingConnectedStatus()
    {
        var (actor, factory, _) = CreateActor();
        var agent = Substitute.For<IAgentActor>();
        var tools = Substitute.For<IToolRegistryActor>();

        factory.GetGrain<IAgentActor>("ws-1/researcher", null).Returns(agent);
        factory.GetGrain<IToolRegistryActor>("ws-1", null).Returns(tools);
        tools.GetConnectionAsync("search").Returns(new ToolConnection { ToolName = "search", ToolType = "mcp", Status = ToolConnectionStatus.Connected });
        tools.GetConnectionAsync("offline").Returns(new ToolConnection { ToolName = "offline", ToolType = "mcp", Status = ToolConnectionStatus.Error });

        await actor.ActivateAllAsync(CreateManifest(("researcher", ["search", "offline"])));

        await agent.Received(1).ConnectToolAsync("search");
        await agent.DidNotReceive().ConnectToolAsync("offline");
    }

    [Fact]
    public async Task ActivateAllAsync_WhenAgentActivationThrows_ExceptionPropagates()
    {
        var (actor, factory, _) = CreateActor();
        var agent = Substitute.For<IAgentActor>();
        factory.GetGrain<IAgentActor>("ws-1/researcher", null).Returns(agent);
        agent.ActivateAgentAsync(Arg.Any<WorkspaceId>(), Arg.Any<AgentDefinition>())
            .Returns(Task.FromException<AgentState>(new InvalidOperationException("activation failed")));

        await Should.ThrowAsync<InvalidOperationException>(
            () => actor.ActivateAllAsync(CreateManifest(("researcher", []))));
    }

    [Fact]
    public async Task DeactivateAllAsync_CallsDeactivateOnEachStoredAgent()
    {
        var (actor, factory, state) = CreateActor(new AgentSupervisorState
        {
            AgentNames = ["researcher", "coder"],
            WorkspaceId = TestWorkspaceId.ToString()
        });
        var researcher = Substitute.For<IAgentActor>();
        var coder = Substitute.For<IAgentActor>();
        factory.GetGrain<IAgentActor>("ws-1/researcher", null).Returns(researcher);
        factory.GetGrain<IAgentActor>("ws-1/coder", null).Returns(coder);

        await actor.DeactivateAllAsync();

        await researcher.Received(1).DeactivateAsync();
        await coder.Received(1).DeactivateAsync();
        state.State.AgentNames.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeactivateAllAsync_WhenOneAgentThrows_OtherAgentsStillDeactivated()
    {
        var (actor, factory, state) = CreateActor(new AgentSupervisorState
        {
            AgentNames = ["broken", "survivor"],
            WorkspaceId = TestWorkspaceId.ToString()
        });
        var broken = Substitute.For<IAgentActor>();
        var survivor = Substitute.For<IAgentActor>();
        broken.DeactivateAsync().Returns(Task.FromException(new InvalidOperationException("boom")));
        factory.GetGrain<IAgentActor>("ws-1/broken", null).Returns(broken);
        factory.GetGrain<IAgentActor>("ws-1/survivor", null).Returns(survivor);

        await actor.DeactivateAllAsync();

        await survivor.Received(1).DeactivateAsync();
        state.State.AgentNames.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetAllAgentStatesAsync_FetchesStateFromEachAgentActor()
    {
        var (actor, factory, _) = CreateActor(new AgentSupervisorState
        {
            AgentNames = ["a", "b"],
            WorkspaceId = TestWorkspaceId.ToString()
        });
        var aActor = Substitute.For<IAgentActor>();
        var bActor = Substitute.For<IAgentActor>();
        aActor.GetStateAsync().Returns(new AgentState { AgentId = "ws-1/a", WorkspaceId = TestWorkspaceId, AgentName = "a" });
        bActor.GetStateAsync().Returns(new AgentState { AgentId = "ws-1/b", WorkspaceId = TestWorkspaceId, AgentName = "b" });
        factory.GetGrain<IAgentActor>("ws-1/a", null).Returns(aActor);
        factory.GetGrain<IAgentActor>("ws-1/b", null).Returns(bActor);

        var states = await actor.GetAllAgentStatesAsync();

        states.Count.ShouldBe(2);
        states.ShouldContain(s => s.AgentName == "a");
        states.ShouldContain(s => s.AgentName == "b");
    }

    [Fact]
    public async Task GetAgentStateAsync_UnknownAgent_ReturnsNullWithoutActorLookup()
    {
        var (actor, factory, _) = CreateActor(new AgentSupervisorState
        {
            AgentNames = ["known"],
            WorkspaceId = TestWorkspaceId.ToString()
        });

        var state = await actor.GetAgentStateAsync("nope");

        state.ShouldBeNull();
        factory.DidNotReceive().GetGrain<IAgentActor>("ws-1/nope", null);
    }

    [Fact]
    public async Task GetAgentStateAsync_KnownAgent_ReturnsActorState()
    {
        var (actor, factory, _) = CreateActor(new AgentSupervisorState
        {
            AgentNames = ["known"],
            WorkspaceId = TestWorkspaceId.ToString()
        });
        var known = Substitute.For<IAgentActor>();
        known.GetStateAsync().Returns(new AgentState
        {
            AgentId = "ws-1/known",
            WorkspaceId = TestWorkspaceId,
            AgentName = "known",
            Status = AgentStatus.Active
        });
        factory.GetGrain<IAgentActor>("ws-1/known", null).Returns(known);

        var state = await actor.GetAgentStateAsync("known");

        state.ShouldNotBeNull();
        state.Status.ShouldBe(AgentStatus.Active);
    }
}
