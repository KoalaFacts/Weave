using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Shared.Ids;
using Weave.Workspaces.Models;

namespace Weave.Agents.Tests;

public sealed class AgentSupervisorGrainTests
{
    private static readonly WorkspaceId TestWorkspaceId = WorkspaceId.From("ws-1");

    private static WorkspaceManifest CreateManifest() => new()
    {
        Name = "test-workspace",
        Version = "1.0",
        Agents = new Dictionary<string, AgentDefinition>
        {
            ["researcher"] = new() { Model = "claude-sonnet-4-20250514", Tools = ["web-search"] },
            ["coder"] = new() { Model = "gpt-4", Tools = [] }
        },
        Tools = new Dictionary<string, ToolDefinition>
        {
            ["web-search"] = new() { Type = "mcp" }
        }
    };

    [Fact]
    public async Task ActivateAllAsync_DelegatesToGrain()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var supervisor = Substitute.For<IAgentSupervisorGrain>();
        grainFactory.GetGrain<IAgentSupervisorGrain>(TestWorkspaceId.ToString(), null)
            .Returns(supervisor);

        var manifest = CreateManifest();
        var grain = grainFactory.GetGrain<IAgentSupervisorGrain>(TestWorkspaceId.ToString());

        await grain.ActivateAllAsync(manifest);

        await supervisor.Received(1).ActivateAllAsync(manifest);
    }

    [Fact]
    public async Task DeactivateAllAsync_DelegatesToGrain()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var supervisor = Substitute.For<IAgentSupervisorGrain>();
        grainFactory.GetGrain<IAgentSupervisorGrain>(TestWorkspaceId.ToString(), null)
            .Returns(supervisor);

        var grain = grainFactory.GetGrain<IAgentSupervisorGrain>(TestWorkspaceId.ToString());

        await grain.DeactivateAllAsync();

        await supervisor.Received(1).DeactivateAllAsync();
    }

    [Fact]
    public async Task GetAllAgentStatesAsync_ReturnsAllStates()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var supervisor = Substitute.For<IAgentSupervisorGrain>();
        IReadOnlyList<AgentState> expectedStates =
        [
            new AgentState { AgentId = "ws-1/researcher", WorkspaceId = TestWorkspaceId, AgentName = "researcher", Status = AgentStatus.Active },
            new AgentState { AgentId = "ws-1/coder", WorkspaceId = TestWorkspaceId, AgentName = "coder", Status = AgentStatus.Busy }
        ];
        supervisor.GetAllAgentStatesAsync().Returns(expectedStates);
        grainFactory.GetGrain<IAgentSupervisorGrain>(TestWorkspaceId.ToString(), null)
            .Returns(supervisor);

        var grain = grainFactory.GetGrain<IAgentSupervisorGrain>(TestWorkspaceId.ToString());
        var states = await grain.GetAllAgentStatesAsync();

        states.Count.ShouldBe(2);
        states.ShouldContain(s => s.AgentName == "researcher" && s.Status == AgentStatus.Active);
        states.ShouldContain(s => s.AgentName == "coder" && s.Status == AgentStatus.Busy);
    }

    [Fact]
    public async Task GetAgentStateAsync_KnownAgent_ReturnsState()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var supervisor = Substitute.For<IAgentSupervisorGrain>();
        var expectedState = new AgentState
        {
            AgentId = "ws-1/researcher",
            WorkspaceId = TestWorkspaceId,
            AgentName = "researcher",
            Status = AgentStatus.Active,
            Model = "claude-sonnet-4-20250514"
        };
        supervisor.GetAgentStateAsync("researcher").Returns(expectedState);
        grainFactory.GetGrain<IAgentSupervisorGrain>(TestWorkspaceId.ToString(), null)
            .Returns(supervisor);

        var grain = grainFactory.GetGrain<IAgentSupervisorGrain>(TestWorkspaceId.ToString());
        var state = await grain.GetAgentStateAsync("researcher");

        state.ShouldNotBeNull();
        state.AgentName.ShouldBe("researcher");
        state.Status.ShouldBe(AgentStatus.Active);
        state.Model.ShouldBe("claude-sonnet-4-20250514");
    }

    [Fact]
    public async Task GetAgentStateAsync_UnknownAgent_ReturnsNull()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var supervisor = Substitute.For<IAgentSupervisorGrain>();
        supervisor.GetAgentStateAsync("nonexistent").Returns((AgentState?)null);
        grainFactory.GetGrain<IAgentSupervisorGrain>(TestWorkspaceId.ToString(), null)
            .Returns(supervisor);

        var grain = grainFactory.GetGrain<IAgentSupervisorGrain>(TestWorkspaceId.ToString());
        var state = await grain.GetAgentStateAsync("nonexistent");

        state.ShouldBeNull();
    }

    [Fact]
    public async Task GetAllAgentStatesAsync_WhenNoAgents_ReturnsEmpty()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var supervisor = Substitute.For<IAgentSupervisorGrain>();
        supervisor.GetAllAgentStatesAsync().Returns(Array.Empty<AgentState>());
        grainFactory.GetGrain<IAgentSupervisorGrain>(TestWorkspaceId.ToString(), null)
            .Returns(supervisor);

        var grain = grainFactory.GetGrain<IAgentSupervisorGrain>(TestWorkspaceId.ToString());
        var states = await grain.GetAllAgentStatesAsync();

        states.ShouldBeEmpty();
    }
}
