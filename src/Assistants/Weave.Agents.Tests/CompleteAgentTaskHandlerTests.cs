using Weave.Agents.Commands;
using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Shared.Ids;

namespace Weave.Agents.Tests;

public sealed class CompleteAgentTaskHandlerTests
{
    private static readonly WorkspaceId _testWorkspaceId = WorkspaceId.From("ws-1");
    private static readonly AgentTaskId _testTaskId = AgentTaskId.From("task-1");

    [Fact]
    public async Task HandleAsync_CompletesTaskSuccessfully()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var agentGrain = Substitute.For<IAgentGrain>();

        grainFactory.GetGrain<IAgentGrain>($"{_testWorkspaceId}/researcher", null)
            .Returns(agentGrain);

        agentGrain.GetStateAsync().Returns(new AgentState
        {
            AgentId = $"{_testWorkspaceId}/researcher",
            WorkspaceId = _testWorkspaceId,
            AgentName = "researcher",
            ActiveTasks =
            [
                new AgentTaskInfo
                {
                    TaskId = _testTaskId,
                    Description = "Fix bug",
                    Status = AgentTaskStatus.Completed,
                    CompletedAt = DateTimeOffset.UtcNow
                }
            ]
        });

        var handler = new CompleteAgentTaskHandler(grainFactory);
        var command = new CompleteAgentTaskCommand(_testWorkspaceId, "researcher", _testTaskId, true);

        var result = await handler.HandleAsync(command, CancellationToken.None);

        result.TaskId.ShouldBe(_testTaskId);
        result.Status.ShouldBe(AgentTaskStatus.Completed);
        await agentGrain.Received(1).CompleteTaskAsync(_testTaskId, true);
    }

    [Fact]
    public async Task HandleAsync_FailsTask()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var agentGrain = Substitute.For<IAgentGrain>();

        grainFactory.GetGrain<IAgentGrain>($"{_testWorkspaceId}/researcher", null)
            .Returns(agentGrain);

        agentGrain.GetStateAsync().Returns(new AgentState
        {
            AgentId = $"{_testWorkspaceId}/researcher",
            WorkspaceId = _testWorkspaceId,
            AgentName = "researcher",
            ActiveTasks =
            [
                new AgentTaskInfo
                {
                    TaskId = _testTaskId,
                    Description = "Fix bug",
                    Status = AgentTaskStatus.Failed
                }
            ]
        });

        var handler = new CompleteAgentTaskHandler(grainFactory);
        var command = new CompleteAgentTaskCommand(_testWorkspaceId, "researcher", _testTaskId, false);

        var result = await handler.HandleAsync(command, CancellationToken.None);

        result.Status.ShouldBe(AgentTaskStatus.Failed);
        await agentGrain.Received(1).CompleteTaskAsync(_testTaskId, false);
    }
}
