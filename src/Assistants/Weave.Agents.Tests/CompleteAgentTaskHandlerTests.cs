using Weave.Agents.Commands;
using Weave.Agents.Grains;
using Weave.Agents.Models;
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
        var grainFactory = Substitute.For<IGrainFactory>();
        var agentGrain = Substitute.For<IAgentGrain>();
        var proof = CreateProof();

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
                    Status = AgentTaskStatus.AwaitingReview,
                    Proof = proof
                }
            ]
        });

        var handler = new CompleteAgentTaskHandler(grainFactory);
        var command = new CompleteAgentTaskCommand(TestWorkspaceId, "researcher", TestTaskId, true, proof);

        var result = await handler.HandleAsync(command, CancellationToken.None);

        result.TaskId.ShouldBe(TestTaskId);
        result.Status.ShouldBe(AgentTaskStatus.AwaitingReview);
        result.Proof.ShouldNotBeNull();
        #pragma warning disable xUnit1051
        await agentGrain.Received(1).CompleteTaskAsync(TestTaskId, true, proof);
        #pragma warning restore xUnit1051
    }

    [Fact]
    public async Task HandleAsync_FailsTaskWithProof()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var agentGrain = Substitute.For<IAgentGrain>();
        var proof = CreateProof(ProofType.Custom, "error details");

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
                    Status = AgentTaskStatus.Failed,
                    Proof = proof
                }
            ]
        });

        var handler = new CompleteAgentTaskHandler(grainFactory);
        var command = new CompleteAgentTaskCommand(TestWorkspaceId, "researcher", TestTaskId, false, proof);

        var result = await handler.HandleAsync(command, CancellationToken.None);

        result.Status.ShouldBe(AgentTaskStatus.Failed);
        #pragma warning disable xUnit1051
        await agentGrain.Received(1).CompleteTaskAsync(TestTaskId, false, proof);
        #pragma warning restore xUnit1051
    }

    [Fact]
    public async Task HandleAsync_WithMultipleProofItems_PassesAll()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var agentGrain = Substitute.For<IAgentGrain>();

        grainFactory.GetGrain<IAgentGrain>($"{TestWorkspaceId}/researcher", null)
            .Returns(agentGrain);

        var proof = new ProofOfWork
        {
            Items =
            [
                new ProofItem { Type = ProofType.CiStatus, Label = "CI", Value = "passed" },
                new ProofItem { Type = ProofType.PullRequest, Label = "PR", Value = "#42", Uri = "https://github.com/org/repo/pull/42" }
            ]
        };

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
                    Status = AgentTaskStatus.AwaitingReview,
                    Proof = proof
                }
            ]
        });

        var handler = new CompleteAgentTaskHandler(grainFactory);
        var command = new CompleteAgentTaskCommand(TestWorkspaceId, "researcher", TestTaskId, true, proof);

        var result = await handler.HandleAsync(command, CancellationToken.None);

        result.Status.ShouldBe(AgentTaskStatus.AwaitingReview);
        result.Proof.ShouldNotBeNull();
        result.Proof.Items.Count.ShouldBe(2);
        #pragma warning disable xUnit1051
        await agentGrain.Received(1).CompleteTaskAsync(TestTaskId, true, proof);
        #pragma warning restore xUnit1051
    }
}
