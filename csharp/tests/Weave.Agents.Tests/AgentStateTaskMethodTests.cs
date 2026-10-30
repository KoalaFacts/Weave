using Weave.Agents.Lifecycle;
using Weave.Agents.Verification;
using Weave.Shared.Ids;

namespace Weave.Agents.Tests;

public sealed class AgentStateTaskMethodTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static AgentState CreateActiveState(int maxTasks = 2) =>
        new()
        {
            AgentId = "ws-1/researcher",
            WorkspaceId = WorkspaceId.From("ws-1"),
            AgentName = "researcher",
            Status = AgentStatus.Active,
            MaxConcurrentTasks = maxTasks
        };

    private static ProofOfWork CreateProof(string label = "CI", string value = "passed") =>
        new()
        {
            Items = [new ProofItem { Type = ProofType.CiStatus, Label = label, Value = value }]
        };

    // --- GetTask ---

    [Fact]
    public void GetTask_ExistingId_ReturnsTask()
    {
        var state = CreateActiveState();
        var submitted = state.SubmitTask("Fix bug", Now);

        var found = state.GetTask(submitted.TaskId);

        found.ShouldBeSameAs(submitted);
    }

    [Fact]
    public void GetTask_UnknownId_Throws()
    {
        var state = CreateActiveState();

        var ex = Should.Throw<InvalidOperationException>(() => state.GetTask(AgentTaskId.From("nonexistent")));
        ex.Message.ShouldContain("not found");
    }

    // --- SubmitTask ---

    [Fact]
    public void SubmitTask_UnderCapacity_ReturnsRunningTask()
    {
        var state = CreateActiveState();

        var task = state.SubmitTask("Fix bug", Now);

        task.TaskId.IsEmpty.ShouldBeFalse();
        task.Description.ShouldBe("Fix bug");
        task.Status.ShouldBe(AgentTaskStatus.Running);
    }

    [Fact]
    public void SubmitTask_UnderCapacity_SetsBusyStatus()
    {
        var state = CreateActiveState();

        state.SubmitTask("Fix bug", Now);

        state.Status.ShouldBe(AgentStatus.Busy);
    }

    [Fact]
    public void SubmitTask_AtCapacity_Throws()
    {
        var state = CreateActiveState(maxTasks: 1);
        state.SubmitTask("Task 1", Now);

        var ex = Should.Throw<InvalidOperationException>(() => state.SubmitTask("Task 2", Now));
        ex.Message.ShouldContain("Max concurrent");
    }

    // --- FailTask ---

    [Fact]
    public void FailTask_SetsFailedStatus()
    {
        var state = CreateActiveState();
        var task = state.SubmitTask("Fix bug", Now);

        state.FailTask(task.TaskId, CreateProof(), Now);

        var updated = state.GetTask(task.TaskId);
        updated.Status.ShouldBe(AgentTaskStatus.Failed);
        updated.CompletedAt.ShouldNotBeNull();
        updated.Proof.ShouldNotBeNull();
    }

    [Fact]
    public void FailTask_NoRunningTasks_RefreshesToActive()
    {
        var state = CreateActiveState();
        var task = state.SubmitTask("Fix bug", Now);

        state.FailTask(task.TaskId, CreateProof(), Now);

        state.Status.ShouldBe(AgentStatus.Active);
    }

    // --- SetAwaitingReview ---

    [Fact]
    public void SetAwaitingReview_SetsStatusAndProof()
    {
        var state = CreateActiveState();
        var task = state.SubmitTask("Fix bug", Now);

        state.SetAwaitingReview(task.TaskId, CreateProof(), Now);

        var updated = state.GetTask(task.TaskId);
        updated.Status.ShouldBe(AgentTaskStatus.AwaitingReview);
        updated.Proof.ShouldNotBeNull();
    }

    // --- AcceptTask ---

    [Fact]
    public void AcceptTask_SetsAcceptedAndCompletedAt()
    {
        var state = CreateActiveState();
        var task = state.SubmitTask("Fix bug", Now);
        state.SetAwaitingReview(task.TaskId, CreateProof(), Now);

        state.AcceptTask(task.TaskId, "Looks good", null, Now);

        var updated = state.GetTask(task.TaskId);
        updated.Status.ShouldBe(AgentTaskStatus.Accepted);
        updated.CompletedAt.ShouldNotBeNull();
    }

    [Fact]
    public void AcceptTask_IncrementsTotalCompleted()
    {
        var state = CreateActiveState();
        var task = state.SubmitTask("Fix bug", Now);
        state.SetAwaitingReview(task.TaskId, CreateProof(), Now);

        state.AcceptTask(task.TaskId, null, null, Now);

        state.TotalTasksCompleted.ShouldBe(1);
    }

    [Fact]
    public void AcceptTask_WhenNotAwaitingReview_Throws()
    {
        var state = CreateActiveState();
        var task = state.SubmitTask("Fix bug", Now);

        var ex = Should.Throw<InvalidOperationException>(() => state.AcceptTask(task.TaskId, null, null, Now));
        ex.Message.ShouldContain("not awaiting review");
    }

    [Fact]
    public void AcceptTask_AppliesReviewMetadata()
    {
        var state = CreateActiveState();
        var task = state.SubmitTask("Fix bug", Now);
        state.SetAwaitingReview(task.TaskId, CreateProof(), Now);

        state.AcceptTask(task.TaskId, "LGTM", null, Now);

        var updated = state.GetTask(task.TaskId);
        updated.Proof.ShouldNotBeNull();
        updated.Proof!.ReviewFeedback.ShouldBe("LGTM");
        updated.Proof.ReviewedAt.ShouldNotBeNull();
    }

    // --- RejectTask ---

    [Fact]
    public void RejectTask_SetsRejectedStatus()
    {
        var state = CreateActiveState();
        var task = state.SubmitTask("Fix bug", Now);
        state.SetAwaitingReview(task.TaskId, CreateProof(), Now);

        state.RejectTask(task.TaskId, "CI is red", null, Now);

        var updated = state.GetTask(task.TaskId);
        updated.Status.ShouldBe(AgentTaskStatus.Rejected);
        updated.CompletedAt.ShouldBeNull();
    }

    [Fact]
    public void RejectTask_WhenNotAwaitingReview_Throws()
    {
        var state = CreateActiveState();
        var task = state.SubmitTask("Fix bug", Now);

        var ex = Should.Throw<InvalidOperationException>(() => state.RejectTask(task.TaskId, null, null, Now));
        ex.Message.ShouldContain("not awaiting review");
    }

    // --- RunningTaskCount ---

    [Fact]
    public void RunningTaskCount_ReturnsCorrectCount()
    {
        var state = CreateActiveState(maxTasks: 3);
        state.SubmitTask("Task 1", Now);
        state.SubmitTask("Task 2", Now);

        state.RunningTaskCount.ShouldBe(2);
    }

    // --- RefreshBusyStatus ---

    [Fact]
    public void RefreshBusyStatus_NoRunning_SetsActive()
    {
        var state = CreateActiveState();
        var task = state.SubmitTask("Task", Now);
        state.FailTask(task.TaskId, CreateProof(), Now);

        state.Status.ShouldBe(AgentStatus.Active);
    }

    [Fact]
    public void RefreshBusyStatus_HasRunning_StaysBusy()
    {
        var state = CreateActiveState(maxTasks: 2);
        state.SubmitTask("Task 1", Now);
        var task2 = state.SubmitTask("Task 2", Now);
        state.FailTask(task2.TaskId, CreateProof(), Now);

        state.Status.ShouldBe(AgentStatus.Busy);
    }
}
