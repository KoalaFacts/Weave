using Weave.Agents.Channels;
using Weave.Agents.Lifecycle;
using Weave.Agents.Memory;
using Weave.Agents.Verification;
using Weave.Agents.Skills;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;
using Weave.Shared.Ids;

namespace Weave.Agents.Tests;

public sealed class CompleteAgentTaskHandlerTests
{
    private static readonly WorkspaceId TestWorkspaceId = WorkspaceId.From("ws-1");
    private static readonly AgentTaskId TestTaskId = AgentTaskId.From("task-1");

    private static ProofOfWork CreateProof(ProofType type = ProofType.CiStatus, string value = "passed") =>
        new()
        {
            Items = [new ProofItem { Type = type, Label = "Evidence", Value = value }]
        };

    [Fact]
    public async Task HandleAsync_SubmitsProofAndReturnsAwaitingReview()
    {
        var actors = Substitute.For<IVirtualActorProvider>();
        var agentActor = Substitute.For<IAgentActor>();
        var proof = CreateProof();

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
                    Status = AgentTaskStatus.AwaitingReview,
                    Proof = proof
                }
            ]
        });

        var handler = new CompleteAgentTaskHandler(actors);
        var command = new CompleteAgentTaskCommand(TestWorkspaceId, "researcher", TestTaskId, true, proof);

        var result = await handler.HandleAsync(command, CancellationToken.None);

        result.TaskId.ShouldBe(TestTaskId);
        result.Status.ShouldBe(AgentTaskStatus.AwaitingReview);
        result.Proof.ShouldNotBeNull();
#pragma warning disable xUnit1051
        await agentActor.Received(1).CompleteTaskAsync(TestTaskId, true, proof);
#pragma warning restore xUnit1051
    }

    [Fact]
    public async Task HandleAsync_FailsTaskWithProof()
    {
        var actors = Substitute.For<IVirtualActorProvider>();
        var agentActor = Substitute.For<IAgentActor>();
        var proof = CreateProof(ProofType.Custom, "error details");

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
                    Status = AgentTaskStatus.Failed,
                    Proof = proof
                }
            ]
        });

        var handler = new CompleteAgentTaskHandler(actors);
        var command = new CompleteAgentTaskCommand(TestWorkspaceId, "researcher", TestTaskId, false, proof);

        var result = await handler.HandleAsync(command, CancellationToken.None);

        result.Status.ShouldBe(AgentTaskStatus.Failed);
#pragma warning disable xUnit1051
        await agentActor.Received(1).CompleteTaskAsync(TestTaskId, false, proof);
#pragma warning restore xUnit1051
    }

    [Fact]
    public async Task HandleAsync_WithMultipleProofItems_PassesAll()
    {
        var actors = Substitute.For<IVirtualActorProvider>();
        var agentActor = Substitute.For<IAgentActor>();

        actors.GetActor<IAgentActor>(Arg.Any<VirtualActorId>())
            .Returns(agentActor);

        var proof = new ProofOfWork
        {
            Items =
            [
                new ProofItem { Type = ProofType.CiStatus, Label = "CI", Value = "passed" },
                new ProofItem { Type = ProofType.PullRequest, Label = "PR", Value = "#42", Uri = "https://github.com/org/repo/pull/42" }
            ]
        };

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
                    Status = AgentTaskStatus.AwaitingReview,
                    Proof = proof
                }
            ]
        });

        var handler = new CompleteAgentTaskHandler(actors);
        var command = new CompleteAgentTaskCommand(TestWorkspaceId, "researcher", TestTaskId, true, proof);

        var result = await handler.HandleAsync(command, CancellationToken.None);

        result.Status.ShouldBe(AgentTaskStatus.AwaitingReview);
        result.Proof.ShouldNotBeNull();
        result.Proof.Items.Count.ShouldBe(2);
#pragma warning disable xUnit1051
        await agentActor.Received(1).CompleteTaskAsync(TestTaskId, true, proof);
#pragma warning restore xUnit1051
    }
}
