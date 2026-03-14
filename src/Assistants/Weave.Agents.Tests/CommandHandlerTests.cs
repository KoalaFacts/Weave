using Weave.Agents.Commands;
using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Agents.Queries;
using Weave.Shared.Ids;
using Weave.Workspaces.Models;

namespace Weave.Agents.Tests;

public sealed class CommandHandlerTests
{
    private static readonly WorkspaceId TestWorkspaceId = WorkspaceId.From("ws-1");

    [Fact]
    public async Task ActivateAgentHandler_DelegatesToGrain()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var agentGrain = Substitute.For<IAgentGrain>();
        var expectedState = new AgentState
        {
            AgentId = $"{TestWorkspaceId}/researcher",
            WorkspaceId = TestWorkspaceId,
            AgentName = "researcher",
            Status = AgentStatus.Active,
            Model = "claude-sonnet-4-20250514"
        };

        grainFactory.GetGrain<IAgentGrain>($"{TestWorkspaceId}/researcher", null)
            .Returns(agentGrain);
        agentGrain.ActivateAgentAsync(TestWorkspaceId, Arg.Any<AgentDefinition>())
            .Returns(expectedState);

        var handler = new ActivateAgentHandler(grainFactory);
        var definition = new AgentDefinition { Model = "claude-sonnet-4-20250514" };
        var command = new ActivateAgentCommand(TestWorkspaceId, "researcher", definition);

        var result = await handler.HandleAsync(command, CancellationToken.None);

        result.Status.ShouldBe(AgentStatus.Active);
        result.Model.ShouldBe("claude-sonnet-4-20250514");
        await agentGrain.Received(1).ActivateAgentAsync(TestWorkspaceId, definition);
    }

    [Fact]
    public async Task DeactivateAgentHandler_DelegatesToGrain()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var agentGrain = Substitute.For<IAgentGrain>();

        grainFactory.GetGrain<IAgentGrain>($"{TestWorkspaceId}/researcher", null)
            .Returns(agentGrain);

        var handler = new DeactivateAgentHandler(grainFactory);
        var command = new DeactivateAgentCommand(TestWorkspaceId, "researcher");

        var result = await handler.HandleAsync(command, CancellationToken.None);

        result.ShouldBeTrue();
        await agentGrain.Received(1).DeactivateAsync();
    }

    [Fact]
    public async Task SubmitAgentTaskHandler_DelegatesToGrain()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var agentGrain = Substitute.For<IAgentGrain>();
        var expectedTask = new AgentTaskInfo
        {
            TaskId = AgentTaskId.From("task-1"),
            Description = "Fix the bug",
            Status = AgentTaskStatus.Running
        };

        grainFactory.GetGrain<IAgentGrain>($"{TestWorkspaceId}/researcher", null)
            .Returns(agentGrain);
        agentGrain.SubmitTaskAsync("Fix the bug")
            .Returns(expectedTask);

        var handler = new SubmitAgentTaskHandler(grainFactory);
        var command = new SubmitAgentTaskCommand(TestWorkspaceId, "researcher", "Fix the bug");

        var result = await handler.HandleAsync(command, CancellationToken.None);

        result.TaskId.ShouldBe(AgentTaskId.From("task-1"));
        result.Description.ShouldBe("Fix the bug");
        result.Status.ShouldBe(AgentTaskStatus.Running);
    }

    [Fact]
    public async Task GetAgentStateHandler_DelegatesToGrain()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var agentGrain = Substitute.For<IAgentGrain>();
        var expectedState = new AgentState
        {
            AgentId = $"{TestWorkspaceId}/researcher",
            WorkspaceId = TestWorkspaceId,
            AgentName = "researcher",
            Status = AgentStatus.Busy
        };

        grainFactory.GetGrain<IAgentGrain>($"{TestWorkspaceId}/researcher", null)
            .Returns(agentGrain);
        agentGrain.GetStateAsync().Returns(expectedState);

        var handler = new GetAgentStateHandler(grainFactory);
        var query = new GetAgentStateQuery(TestWorkspaceId, "researcher");

        var result = await handler.HandleAsync(query, CancellationToken.None);

        result.Status.ShouldBe(AgentStatus.Busy);
        result.AgentName.ShouldBe("researcher");
    }

    [Fact]
    public async Task GetAllAgentStatesHandler_DelegatesToSupervisor()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var supervisor = Substitute.For<IAgentSupervisorGrain>();
        IReadOnlyList<AgentState> expectedStates =
        [
            new AgentState { AgentId = "ws-1/a1", WorkspaceId = TestWorkspaceId, AgentName = "a1", Status = AgentStatus.Active },
            new AgentState { AgentId = "ws-1/a2", WorkspaceId = TestWorkspaceId, AgentName = "a2", Status = AgentStatus.Busy }
        ];

        grainFactory.GetGrain<IAgentSupervisorGrain>(TestWorkspaceId.ToString(), null)
            .Returns(supervisor);
        supervisor.GetAllAgentStatesAsync().Returns(expectedStates);

        var handler = new GetAllAgentStatesHandler(grainFactory);
        var query = new GetAllAgentStatesQuery(TestWorkspaceId);

        var result = await handler.HandleAsync(query, CancellationToken.None);

        result.Count.ShouldBe(2);
        result[0].AgentName.ShouldBe("a1");
        result[1].AgentName.ShouldBe("a2");
    }
}
