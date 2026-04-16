using Microsoft.Extensions.Logging;
using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Agents.Pipeline;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;
using Weave.Workspaces.Models;

namespace Weave.Agents.Tests;

public sealed class AgentGrainTests
{
    private static readonly WorkspaceId TestWorkspaceId = WorkspaceId.From("ws-1");
    private const string TestAgentName = "researcher";

    private static IPersistentState<AgentState> CreatePersistentState()
    {
        var state = new AgentState
        {
            AgentId = $"{TestWorkspaceId}/{TestAgentName}",
            WorkspaceId = TestWorkspaceId,
            AgentName = TestAgentName
        };

        var persistentState = Substitute.For<IPersistentState<AgentState>>();
        persistentState.State.Returns(state);
        persistentState.ReadStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync().Returns(Task.CompletedTask);
        persistentState.ClearStateAsync().Returns(Task.CompletedTask);
        return persistentState;
    }

    private static AgentDefinition CreateDefinition(string model = "claude-sonnet-4-20250514", int maxTasks = 2) =>
        new()
        {
            Model = model,
            MaxConcurrentTasks = maxTasks,
            Tools = ["code-search", "shell"]
        };

    private static (AgentGrain Grain, ILifecycleManager Lifecycle, IEventBus EventBus, ISkillMemoryGrain SkillMemory) CreateGrain()
    {
        var skillMemory = Substitute.For<ISkillMemoryGrain>();
        skillMemory.StoreSkillAsync(Arg.Any<SkillDocument>())
            .Returns(callInfo => Task.FromResult(callInfo.Arg<SkillDocument>()));

        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<ISkillMemoryGrain>(Arg.Any<string>(), null).Returns(skillMemory);

        var chatPipeline = Substitute.For<IAgentChatPipeline>();
        var lifecycle = Substitute.For<ILifecycleManager>();
        var eventBus = Substitute.For<IEventBus>();
        var logger = Substitute.For<ILogger<AgentGrain>>();
        var persistentState = CreatePersistentState();

        var grain = new AgentGrain(grainFactory, chatPipeline, lifecycle, eventBus, logger, persistentState);
        return (grain, lifecycle, eventBus, skillMemory);
    }

    [Fact]
    public async Task ActivateAgentAsync_SetsActiveStatus()
    {
        var (grain, _, _, _) = CreateGrain();
        var definition = CreateDefinition();

        var state = await grain.ActivateAgentAsync(TestWorkspaceId, definition);

        state.Status.ShouldBe(AgentStatus.Active);
        state.Model.ShouldBe("claude-sonnet-4-20250514");
        state.ActivatedAt.ShouldNotBeNull();
        state.MaxConcurrentTasks.ShouldBe(2);
    }

    [Fact]
    public async Task ActivateAgentAsync_RunsLifecycleHooks()
    {
        var (grain, lifecycle, _, _) = CreateGrain();

        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());

        await lifecycle.Received(1).RunHooksAsync(
            LifecyclePhase.AgentActivating,
            Arg.Any<LifecycleContext>(),
            Arg.Any<CancellationToken>());
        await lifecycle.Received(1).RunHooksAsync(
            LifecyclePhase.AgentActivated,
            Arg.Any<LifecycleContext>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActivateAgentAsync_PublishesEvent()
    {
        var (grain, _, eventBus, _) = CreateGrain();

        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());

        await eventBus.Received(1).PublishAsync(
            Arg.Is<Events.AgentActivatedEvent>(e =>
                e.WorkspaceId == TestWorkspaceId &&
                e.Model == "claude-sonnet-4-20250514"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActivateAgentAsync_WhenAlreadyActive_ReturnsCurrentState()
    {
        var (grain, _, _, _) = CreateGrain();
        var definition = CreateDefinition();

        var first = await grain.ActivateAgentAsync(TestWorkspaceId, definition);
        var second = await grain.ActivateAgentAsync(TestWorkspaceId, definition);

        second.ShouldBeSameAs(first);
    }

    [Fact]
    public async Task DeactivateAsync_SetsIdleStatus()
    {
        var (grain, _, _, _) = CreateGrain();
        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());

        await grain.DeactivateAsync();

        var state = await grain.GetStateAsync();
        state.Status.ShouldBe(AgentStatus.Idle);
        state.DeactivatedAt.ShouldNotBeNull();
        state.ConnectedTools.ShouldBeEmpty();
        state.ActiveTasks.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeactivateAsync_PublishesEvent()
    {
        var (grain, _, eventBus, _) = CreateGrain();
        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());

        await grain.DeactivateAsync();

        await eventBus.Received(1).PublishAsync(
            Arg.Is<Events.AgentDeactivatedEvent>(e => e.WorkspaceId == TestWorkspaceId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitTaskAsync_ReturnsRunningTask()
    {
        var (grain, _, _, _) = CreateGrain();
        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());

        var task = await grain.SubmitTaskAsync("Fix the bug");

        task.TaskId.IsEmpty.ShouldBeFalse();
        task.Description.ShouldBe("Fix the bug");
        task.Status.ShouldBe(AgentTaskStatus.Running);
    }

    [Fact]
    public async Task SubmitTaskAsync_SetsBusyStatus()
    {
        var (grain, _, _, _) = CreateGrain();
        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());

        await grain.SubmitTaskAsync("Fix the bug");

        var state = await grain.GetStateAsync();
        state.Status.ShouldBe(AgentStatus.Busy);
    }

    [Fact]
    public async Task SubmitTaskAsync_WhenAtMaxConcurrent_Throws()
    {
        var (grain, _, _, _) = CreateGrain();
        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition(maxTasks: 1));
        await grain.SubmitTaskAsync("Task 1");

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => grain.SubmitTaskAsync("Task 2"));
        ex.Message.ShouldContain("max concurrent");
    }

    [Fact]
    public async Task SubmitTaskAsync_WhenNotActive_Throws()
    {
        var (grain, _, _, _) = CreateGrain();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => grain.SubmitTaskAsync("Task 1"));
        ex.Message.ShouldContain("not active");
    }

    [Fact]
    public async Task CompleteTaskAsync_SuccessWithProof_SetsAwaitingReview()
    {
        var (grain, _, _, _) = CreateGrain();
        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());
        var task = await grain.SubmitTaskAsync("Fix the bug");
        var proof = new ProofOfWork
        {
            Items = [new ProofItem { Type = ProofType.CiStatus, Label = "CI", Value = "passed" }]
        };

        await grain.CompleteTaskAsync(task.TaskId, success: true, proof);

        var state = await grain.GetStateAsync();
        state.ActiveTasks.ShouldContain(t =>
            t.TaskId == task.TaskId &&
            t.Status == AgentTaskStatus.AwaitingReview &&
            t.Proof != null);
    }

    [Fact]
    public async Task CompleteTaskAsync_SuccessWithProof_DoesNotCompleteYet()
    {
        var (grain, _, _, _) = CreateGrain();
        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());
        var task = await grain.SubmitTaskAsync("Fix the bug");
        var proof = new ProofOfWork
        {
            Items = [new ProofItem { Type = ProofType.CiStatus, Label = "CI", Value = "passed" }]
        };

        await grain.CompleteTaskAsync(task.TaskId, success: true, proof);

        var state = await grain.GetStateAsync();
        var updated = state.ActiveTasks.First(t => t.TaskId == task.TaskId);
        updated.CompletedAt.ShouldBeNull();
        state.TotalTasksCompleted.ShouldBe(0);
    }

    [Fact]
    public async Task CompleteTaskAsync_WithFailure_MarksAsFailed()
    {
        var (grain, _, _, _) = CreateGrain();
        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());
        var task = await grain.SubmitTaskAsync("Fix the bug");
        var proof = new ProofOfWork
        {
            Items = [new ProofItem { Type = ProofType.Custom, Label = "Error", Value = "compilation failed" }]
        };

        await grain.CompleteTaskAsync(task.TaskId, success: false, proof);

        var state = await grain.GetStateAsync();
        state.ActiveTasks.ShouldContain(t =>
            t.TaskId == task.TaskId &&
            t.Status == AgentTaskStatus.Failed);
    }

    [Fact]
    public async Task CompleteTaskAsync_Failure_ReturnsToActive_WhenNoRunningTasks()
    {
        var (grain, _, _, _) = CreateGrain();
        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());
        var task = await grain.SubmitTaskAsync("Fix the bug");
        var proof = new ProofOfWork
        {
            Items = [new ProofItem { Type = ProofType.Custom, Label = "Error", Value = "failed" }]
        };

        await grain.CompleteTaskAsync(task.TaskId, success: false, proof);

        var state = await grain.GetStateAsync();
        state.Status.ShouldBe(AgentStatus.Active);
    }

    [Fact]
    public async Task CompleteTaskAsync_UnknownTaskId_Throws()
    {
        var (grain, _, _, _) = CreateGrain();
        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());
        var proof = new ProofOfWork
        {
            Items = [new ProofItem { Type = ProofType.Custom, Label = "X", Value = "y" }]
        };

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => grain.CompleteTaskAsync(AgentTaskId.From("nonexistent"), success: true, proof));
        ex.Message.ShouldContain("not found");
    }

    [Fact]
    public async Task CompleteTaskAsync_WithProof_PublishesAwaitingReviewEvent()
    {
        var (grain, _, eventBus, _) = CreateGrain();
        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());
        var task = await grain.SubmitTaskAsync("Implement feature");
        var proof = new ProofOfWork
        {
            Items = [new ProofItem { Type = ProofType.TestResults, Label = "Tests", Value = "42 passed" }]
        };

        await grain.CompleteTaskAsync(task.TaskId, success: true, proof);

        await eventBus.Received(1).PublishAsync(
            Arg.Is<Events.AgentTaskAwaitingReviewEvent>(e =>
                e.TaskId == task.TaskId &&
                e.WorkspaceId == TestWorkspaceId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewTaskAsync_Accepted_SetsAcceptedStatus()
    {
        var (grain, _, _, _) = CreateGrain();
        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());
        var task = await grain.SubmitTaskAsync("Implement feature");
        var proof = new ProofOfWork
        {
            Items = [new ProofItem { Type = ProofType.PullRequest, Label = "PR", Value = "#10" }]
        };
        await grain.CompleteTaskAsync(task.TaskId, success: true, proof);

        await grain.ReviewTaskAsync(task.TaskId, accepted: true, "Looks good");

        var state = await grain.GetStateAsync();
        var reviewed = state.ActiveTasks.First(t => t.TaskId == task.TaskId);
        reviewed.Status.ShouldBe(AgentTaskStatus.Accepted);
        reviewed.CompletedAt.ShouldNotBeNull();
        reviewed.Proof!.ReviewFeedback.ShouldBe("Looks good");
        reviewed.Proof.ReviewedAt.ShouldNotBeNull();
        state.TotalTasksCompleted.ShouldBe(1);
    }

    [Fact]
    public async Task ReviewTaskAsync_Rejected_SetsRejectedStatus()
    {
        var (grain, _, _, _) = CreateGrain();
        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());
        var task = await grain.SubmitTaskAsync("Implement feature");
        var proof = new ProofOfWork
        {
            Items = [new ProofItem { Type = ProofType.CiStatus, Label = "CI", Value = "failed" }]
        };
        await grain.CompleteTaskAsync(task.TaskId, success: true, proof);

        await grain.ReviewTaskAsync(task.TaskId, accepted: false, "CI is red");

        var state = await grain.GetStateAsync();
        var reviewed = state.ActiveTasks.First(t => t.TaskId == task.TaskId);
        reviewed.Status.ShouldBe(AgentTaskStatus.Rejected);
        reviewed.CompletedAt.ShouldBeNull();
        reviewed.Proof!.ReviewFeedback.ShouldBe("CI is red");
        state.TotalTasksCompleted.ShouldBe(0);
    }

    [Fact]
    public async Task ReviewTaskAsync_PublishesReviewedEvent()
    {
        var (grain, _, eventBus, _) = CreateGrain();
        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());
        var task = await grain.SubmitTaskAsync("Implement feature");
        var proof = new ProofOfWork
        {
            Items = [new ProofItem { Type = ProofType.DiffSummary, Label = "Diff", Value = "+10 -2" }]
        };
        await grain.CompleteTaskAsync(task.TaskId, success: true, proof);

        await grain.ReviewTaskAsync(task.TaskId, accepted: true);

        await eventBus.Received(1).PublishAsync(
            Arg.Is<Events.AgentTaskReviewedEvent>(e =>
                e.TaskId == task.TaskId &&
                e.Accepted &&
                e.WorkspaceId == TestWorkspaceId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewTaskAsync_WhenNotAwaitingReview_Throws()
    {
        var (grain, _, _, _) = CreateGrain();
        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());
        var task = await grain.SubmitTaskAsync("Task");

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => grain.ReviewTaskAsync(task.TaskId, accepted: true));
        ex.Message.ShouldContain("not awaiting review");
    }

    [Fact]
    public async Task ReviewTaskAsync_UnknownTaskId_Throws()
    {
        var (grain, _, _, _) = CreateGrain();
        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => grain.ReviewTaskAsync(AgentTaskId.From("nonexistent"), accepted: true));
        ex.Message.ShouldContain("not found");
    }

    [Fact]
    public async Task ReviewTaskAsync_Accepted_ReturnsToActive_WhenNoRunningTasks()
    {
        var (grain, _, _, _) = CreateGrain();
        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());
        var task = await grain.SubmitTaskAsync("Feature");
        var proof = new ProofOfWork
        {
            Items = [new ProofItem { Type = ProofType.Custom, Label = "Note", Value = "done" }]
        };
        await grain.CompleteTaskAsync(task.TaskId, success: true, proof);

        await grain.ReviewTaskAsync(task.TaskId, accepted: true);

        var state = await grain.GetStateAsync();
        state.Status.ShouldBe(AgentStatus.Active);
    }

    [Fact]
    public async Task ConnectToolAsync_AddsTool()
    {
        var (grain, _, _, _) = CreateGrain();
        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());

        await grain.ConnectToolAsync("code-search");

        var state = await grain.GetStateAsync();
        state.ConnectedTools.ShouldContain("code-search");
    }

    [Fact]
    public async Task DisconnectToolAsync_RemovesTool()
    {
        var (grain, _, _, _) = CreateGrain();
        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());
        await grain.ConnectToolAsync("code-search");

        await grain.DisconnectToolAsync("code-search");

        var state = await grain.GetStateAsync();
        state.ConnectedTools.ShouldNotContain("code-search");
    }

    [Fact]
    public async Task ReviewTaskAsync_Accepted_WithMultiStepProof_ExtractsSkill()
    {
        var (grain, _, _, skillMemory) = CreateGrain();
        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());
        var task = await grain.SubmitTaskAsync("Deploy the service");
        var proof = new ProofOfWork
        {
            Items =
            [
                new ProofItem { Type = ProofType.CiStatus, Label = "CI", Value = "passed" },
                new ProofItem { Type = ProofType.DiffSummary, Label = "Diff", Value = "+50 -10" },
                new ProofItem { Type = ProofType.PullRequest, Label = "PR", Value = "#42", Uri = "https://github.com/org/repo/pull/42" }
            ]
        };
        await grain.CompleteTaskAsync(task.TaskId, success: true, proof);

        await grain.ReviewTaskAsync(task.TaskId, accepted: true);

        await skillMemory.Received(1).StoreSkillAsync(Arg.Is<SkillDocument>(s =>
            s.Title == "Deploy the service" &&
            s.Steps.Count == 3));
    }

    [Fact]
    public async Task ReviewTaskAsync_Accepted_WithSingleProofItem_DoesNotExtractSkill()
    {
        var (grain, _, _, skillMemory) = CreateGrain();
        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());
        var task = await grain.SubmitTaskAsync("Quick fix");
        var proof = new ProofOfWork
        {
            Items = [new ProofItem { Type = ProofType.CiStatus, Label = "CI", Value = "passed" }]
        };
        await grain.CompleteTaskAsync(task.TaskId, success: true, proof);

        await grain.ReviewTaskAsync(task.TaskId, accepted: true);

        await skillMemory.DidNotReceive().StoreSkillAsync(Arg.Any<SkillDocument>());
    }

    [Fact]
    public async Task ReviewTaskAsync_Rejected_DoesNotExtractSkill()
    {
        var (grain, _, _, skillMemory) = CreateGrain();
        await grain.ActivateAgentAsync(TestWorkspaceId, CreateDefinition());
        var task = await grain.SubmitTaskAsync("Feature work");
        var proof = new ProofOfWork
        {
            Items =
            [
                new ProofItem { Type = ProofType.CiStatus, Label = "CI", Value = "failed" },
                new ProofItem { Type = ProofType.TestResults, Label = "Tests", Value = "3 failed" }
            ]
        };
        await grain.CompleteTaskAsync(task.TaskId, success: true, proof);

        await grain.ReviewTaskAsync(task.TaskId, accepted: false, "Needs more work");

        await skillMemory.DidNotReceive().StoreSkillAsync(Arg.Any<SkillDocument>());
    }

    [Fact]
    public void ExtractSkillFromTask_ReturnsNull_WhenTooFewProofItems()
    {
        var task = new AgentTaskInfo
        {
            TaskId = AgentTaskId.New(),
            Description = "Simple task",
            Proof = new ProofOfWork
            {
                Items = [new ProofItem { Type = ProofType.CiStatus, Label = "CI", Value = "ok" }]
            }
        };

        var result = AgentGrain.ExtractSkillFromTask(task, new AgentState());
        result.ShouldBeNull();
    }

    [Fact]
    public void ExtractSkillFromTask_BuildsDocument_FromMultiStepProof()
    {
        var task = new AgentTaskInfo
        {
            TaskId = AgentTaskId.New(),
            Description = "Build and deploy microservice",
            Proof = new ProofOfWork
            {
                Items =
                [
                    new ProofItem { Type = ProofType.CiStatus, Label = "CI", Value = "green" },
                    new ProofItem { Type = ProofType.Custom, Label = "docker-build", Value = "image built" },
                    new ProofItem { Type = ProofType.DiffSummary, Label = "deploy-yaml", Value = "+20 -5" }
                ]
            }
        };
        var state = new AgentState
        {
            AgentName = "deployer",
            ConnectedTools = ["docker", "kubectl"]
        };

        var skill = AgentGrain.ExtractSkillFromTask(task, state);

        skill.ShouldNotBeNull();
        skill.Title.ShouldBe("Build and deploy microservice");
        skill.Steps.Count.ShouldBe(3);
        skill.CreatedByAgent.ShouldBe("deployer");
        skill.ToolsUsed.ShouldContain("docker");
        skill.ToolsUsed.ShouldContain("kubectl");
        skill.OriginTaskDescription.ShouldBe("Build and deploy microservice");
    }
}
