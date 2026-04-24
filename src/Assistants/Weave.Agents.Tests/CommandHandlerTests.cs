using Weave.Agents.Commands;
using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Agents.Queries;
using Weave.Shared.Ids;
using Weave.Workspaces.Models;

namespace Weave.Agents.Tests;

public sealed class CommandHandlerTests
{
    private static readonly WorkspaceId TestWorkspaceId = WorkspaceId.From("ws-1");

    [Fact]
    public async Task ActivateAgentHandler_DelegatesToActor()
    {
        var actors = Substitute.For<IVirtualActorProvider>();
        var agentActor = Substitute.For<IAgentActor>();
        var expectedState = new AgentState
        {
            AgentId = $"{TestWorkspaceId}/researcher",
            WorkspaceId = TestWorkspaceId,
            AgentName = "researcher",
            Status = AgentStatus.Active,
            Model = "claude-sonnet-4-20250514"
        };

        actors.GetActor<IAgentActor>(Arg.Any<VirtualActorId>())
            .Returns(agentActor);
        agentActor.ActivateAgentAsync(TestWorkspaceId, Arg.Any<AgentDefinition>())
            .Returns(expectedState);

        var handler = new ActivateAgentHandler(actors);
        var definition = new AgentDefinition { Model = "claude-sonnet-4-20250514" };
        var command = new ActivateAgentCommand(TestWorkspaceId, "researcher", definition);

        var result = await handler.HandleAsync(command, CancellationToken.None);

        result.Status.ShouldBe(AgentStatus.Active);
        result.Model.ShouldBe("claude-sonnet-4-20250514");
        await agentActor.Received(1).ActivateAgentAsync(TestWorkspaceId, definition);
    }

    [Fact]
    public async Task DeactivateAgentHandler_DelegatesToActor()
    {
        var actors = Substitute.For<IVirtualActorProvider>();
        var agentActor = Substitute.For<IAgentActor>();

        actors.GetActor<IAgentActor>(Arg.Any<VirtualActorId>())
            .Returns(agentActor);

        var handler = new DeactivateAgentHandler(actors);
        var command = new DeactivateAgentCommand(TestWorkspaceId, "researcher");

        var result = await handler.HandleAsync(command, CancellationToken.None);

        result.ShouldBeTrue();
        await agentActor.Received(1).DeactivateAsync();
    }

    [Fact]
    public async Task SubmitAgentTaskHandler_DelegatesToActor()
    {
        var actors = Substitute.For<IVirtualActorProvider>();
        var agentActor = Substitute.For<IAgentActor>();
        var expectedTask = new AgentTaskInfo
        {
            TaskId = AgentTaskId.From("task-1"),
            Description = "Fix the bug",
            Status = AgentTaskStatus.Running
        };

        actors.GetActor<IAgentActor>(Arg.Any<VirtualActorId>())
            .Returns(agentActor);
        agentActor.SubmitTaskAsync("Fix the bug")
            .Returns(expectedTask);

        var handler = new SubmitAgentTaskHandler(actors);
        var command = new SubmitAgentTaskCommand(TestWorkspaceId, "researcher", "Fix the bug");

        var result = await handler.HandleAsync(command, CancellationToken.None);

        result.TaskId.ShouldBe(AgentTaskId.From("task-1"));
        result.Description.ShouldBe("Fix the bug");
        result.Status.ShouldBe(AgentTaskStatus.Running);
    }

    [Fact]
    public async Task GetAgentStateHandler_DelegatesToActor()
    {
        var actors = Substitute.For<IVirtualActorProvider>();
        var agentActor = Substitute.For<IAgentActor>();
        var expectedState = new AgentState
        {
            AgentId = $"{TestWorkspaceId}/researcher",
            WorkspaceId = TestWorkspaceId,
            AgentName = "researcher",
            Status = AgentStatus.Busy
        };

        actors.GetActor<IAgentActor>(Arg.Any<VirtualActorId>())
            .Returns(agentActor);
        agentActor.GetStateAsync().Returns(expectedState);

        var handler = new GetAgentStateHandler(actors);
        var query = new GetAgentStateQuery(TestWorkspaceId, "researcher");

        var result = await handler.HandleAsync(query, CancellationToken.None);

        result.Status.ShouldBe(AgentStatus.Busy);
        result.AgentName.ShouldBe("researcher");
    }

    [Fact]
    public async Task GetAllAgentStatesHandler_DelegatesToSupervisor()
    {
        var actors = Substitute.For<IVirtualActorProvider>();
        var supervisor = Substitute.For<IAgentSupervisorActor>();
        IReadOnlyList<AgentState> expectedStates =
        [
            new AgentState { AgentId = "ws-1/a1", WorkspaceId = TestWorkspaceId, AgentName = "a1", Status = AgentStatus.Active },
            new AgentState { AgentId = "ws-1/a2", WorkspaceId = TestWorkspaceId, AgentName = "a2", Status = AgentStatus.Busy }
        ];

        actors.GetActor<IAgentSupervisorActor>(Arg.Any<VirtualActorId>())
            .Returns(supervisor);
        supervisor.GetAllAgentStatesAsync().Returns(expectedStates);

        var handler = new GetAllAgentStatesHandler(actors);
        var query = new GetAllAgentStatesQuery(TestWorkspaceId);

        var result = await handler.HandleAsync(query, CancellationToken.None);

        result.Count.ShouldBe(2);
        result[0].AgentName.ShouldBe("a1");
        result[1].AgentName.ShouldBe("a2");
    }
}
