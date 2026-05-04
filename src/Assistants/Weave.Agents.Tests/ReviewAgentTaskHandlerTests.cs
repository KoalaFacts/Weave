using Weave.Agents.Lifecycle;
using Weave.Agents.Channels;
using Weave.Agents.Memory;
using Weave.Agents.Skills;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Models;
using Weave.Shared.Ids;

namespace Weave.Agents.Tests;

public sealed class ReviewAgentTaskHandlerTests
{
    private static readonly WorkspaceId TestWorkspaceId = WorkspaceId.From("ws-1");
    private static readonly AgentTaskId TestTaskId = AgentTaskId.From("task-1");

    [Fact]
    public async Task HandleAsync_AcceptsTask()
    {
        var actors = Substitute.For<IVirtualActorProvider>();
        var agentActor = Substitute.For<IAgentActor>();

        actors.GetActor<IAgentActor>(Arg.Any<VirtualActorId>())
            .Returns(agentActor);

        agentActor.GetStateAsync().Returns(new AgentState
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
                    Status = AgentTaskStatus.Accepted,
                    CompletedAt = DateTimeOffset.UtcNow,
                    Proof = new ProofOfWork
                    {
                        Items = [new ProofItem { Type = ProofType.CiStatus, Label = "CI", Value = "passed" }],
                        ReviewFeedback = "LGTM",
                        ReviewedAt = DateTimeOffset.UtcNow
                    }
                }
            ]
        });

        var handler = new ReviewAgentTaskHandler(actors);
        var command = new ReviewAgentTaskCommand(TestWorkspaceId, "researcher", TestTaskId, true, "LGTM");

        var result = await handler.HandleAsync(command, CancellationToken.None);

        result.TaskId.ShouldBe(TestTaskId);
        result.Status.ShouldBe(AgentTaskStatus.Accepted);
        result.Proof.ShouldNotBeNull();
#pragma warning disable xUnit1051
        await agentActor.Received(1).ReviewTaskAsync(TestTaskId, true, "LGTM");
#pragma warning restore xUnit1051
    }

    [Fact]
    public async Task HandleAsync_RejectsTask()
    {
        var actors = Substitute.For<IVirtualActorProvider>();
        var agentActor = Substitute.For<IAgentActor>();

        actors.GetActor<IAgentActor>(Arg.Any<VirtualActorId>())
            .Returns(agentActor);

        agentActor.GetStateAsync().Returns(new AgentState
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
                    Status = AgentTaskStatus.Rejected,
                    Proof = new ProofOfWork
                    {
                        Items = [new ProofItem { Type = ProofType.TestResults, Label = "Tests", Value = "3 failed" }],
                        ReviewFeedback = "Tests failing",
                        ReviewedAt = DateTimeOffset.UtcNow
                    }
                }
            ]
        });

        var handler = new ReviewAgentTaskHandler(actors);
        var command = new ReviewAgentTaskCommand(TestWorkspaceId, "researcher", TestTaskId, false, "Tests failing");

        var result = await handler.HandleAsync(command, CancellationToken.None);

        result.Status.ShouldBe(AgentTaskStatus.Rejected);
#pragma warning disable xUnit1051
        await agentActor.Received(1).ReviewTaskAsync(TestTaskId, false, "Tests failing");
#pragma warning restore xUnit1051
    }
}
