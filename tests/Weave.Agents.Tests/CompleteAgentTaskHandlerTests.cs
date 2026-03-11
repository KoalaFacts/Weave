using FluentAssertions;
using NSubstitute;
using Orleans;
using Weave.Agents.Commands;
using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Shared.Ids;
using Xunit;

namespace Weave.Agents.Tests;

public sealed class CompleteAgentTaskHandlerTests
{
    private static readonly WorkspaceId TestWorkspaceId = WorkspaceId.From("ws-1");
    private static readonly AgentTaskId TestTaskId = AgentTaskId.From("task-1");

    [Fact]
    public async Task HandleAsync_CompletesTaskSuccessfully()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var agentGrain = Substitute.For<IAgentGrain>();

        grainFactory.GetGrain<IAgentGrain>($"{TestWorkspaceId}/researcher", null)
            .Returns(agentGrain);

        agentGrain.GetStateAsync().Returns(new AgentState
        {
            AgentId = $"{TestWorkspaceId}/researcher",
            WorkspaceId = TestWorkspaceId,
            AgentName = "researcher",
            ActiveTasks =
            [
                new AgentTaskInfo
                {
                    TaskId = TestTaskId,
                    Description = "Fix bug",
                    Status = AgentTaskStatus.Completed,
                    CompletedAt = DateTimeOffset.UtcNow
                }
            ]
        });

        var handler = new CompleteAgentTaskHandler(grainFactory);
        var command = new CompleteAgentTaskCommand(TestWorkspaceId, "researcher", TestTaskId, true);

        var result = await handler.HandleAsync(command, CancellationToken.None);

        result.TaskId.Should().Be(TestTaskId);
        result.Status.Should().Be(AgentTaskStatus.Completed);
        await agentGrain.Received(1).CompleteTaskAsync(TestTaskId, true);
    }

    [Fact]
    public async Task HandleAsync_FailsTask()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var agentGrain = Substitute.For<IAgentGrain>();

        grainFactory.GetGrain<IAgentGrain>($"{TestWorkspaceId}/researcher", null)
            .Returns(agentGrain);

        agentGrain.GetStateAsync().Returns(new AgentState
        {
            AgentId = $"{TestWorkspaceId}/researcher",
            WorkspaceId = TestWorkspaceId,
            AgentName = "researcher",
            ActiveTasks =
            [
                new AgentTaskInfo
                {
                    TaskId = TestTaskId,
                    Description = "Fix bug",
                    Status = AgentTaskStatus.Failed
                }
            ]
        });

        var handler = new CompleteAgentTaskHandler(grainFactory);
        var command = new CompleteAgentTaskCommand(TestWorkspaceId, "researcher", TestTaskId, false);

        var result = await handler.HandleAsync(command, CancellationToken.None);

        result.Status.Should().Be(AgentTaskStatus.Failed);
        await agentGrain.Received(1).CompleteTaskAsync(TestTaskId, false);
    }
}
