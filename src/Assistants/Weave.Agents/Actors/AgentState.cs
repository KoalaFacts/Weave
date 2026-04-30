using Weave.Shared.Ids;
using Weave.Workspaces.Models;

namespace Weave.Agents.Models;

public sealed record AgentState
{
    public string AgentId { get; set; } = string.Empty;
    public WorkspaceId WorkspaceId { get; set; } = WorkspaceId.Empty;
    public string AgentName { get; set; } = string.Empty;
    public AgentStatus Status { get; set; } = AgentStatus.Idle;
    public string? Model { get; set; }
    public List<string> ConnectedTools { get; init; } = [];
    public List<AgentTaskInfo> ActiveTasks { get; init; } = [];
    public int MaxConcurrentTasks { get; set; } = 1;
    public DateTimeOffset? ActivatedAt { get; set; }
    public DateTimeOffset? DeactivatedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public List<ConversationMessage> History { get; init; } = [];
    public DateTimeOffset? LastActive { get; set; }
    public int TotalTasksCompleted { get; set; }
    public AgentDefinition? Definition { get; set; }
    public string? ConversationId { get; set; }
    public int LastEpisodeHistoryIndex { get; set; }

    public int RunningTaskCount =>
        ActiveTasks.Count(task => task.Status is AgentTaskStatus.Running);

    public AgentTaskInfo GetTask(AgentTaskId taskId) =>
        ActiveTasks.FirstOrDefault(task => task.TaskId == taskId)
        ?? throw new InvalidOperationException($"Task {taskId} not found.");

    public AgentTaskInfo SubmitTask(string description)
    {
        if (RunningTaskCount >= MaxConcurrentTasks)
            throw new InvalidOperationException($"Max concurrent tasks ({MaxConcurrentTasks}) reached.");

        var task = new AgentTaskInfo
        {
            TaskId = AgentTaskId.New(),
            Description = description,
            Status = AgentTaskStatus.Running
        };

        ActiveTasks.Add(task);
        Status = AgentStatus.Busy;
        LastActive = DateTimeOffset.UtcNow;
        return task;
    }

    public void FailTask(AgentTaskId taskId, ProofOfWork proof)
    {
        var task = GetTask(taskId);
        task.Status = AgentTaskStatus.Failed;
        task.CompletedAt = DateTimeOffset.UtcNow;
        task.Proof = proof;
        RefreshBusyStatus();
        LastActive = DateTimeOffset.UtcNow;
    }

    public void SetAwaitingReview(AgentTaskId taskId, ProofOfWork proof)
    {
        var task = GetTask(taskId);
        task.Status = AgentTaskStatus.AwaitingReview;
        task.Proof = proof;
        LastActive = DateTimeOffset.UtcNow;
    }

    public void AcceptTask(AgentTaskId taskId, string? feedback, VerificationRecord? verification)
    {
        var task = GetTask(taskId);
        if (task.Status is not AgentTaskStatus.AwaitingReview)
            throw new InvalidOperationException($"Task {taskId} is not awaiting review (status: {task.Status}).");

        ApplyReviewMetadata(task, feedback, verification);
        task.Status = AgentTaskStatus.Accepted;
        task.CompletedAt = DateTimeOffset.UtcNow;
        TotalTasksCompleted++;
        RefreshBusyStatus();
        LastActive = DateTimeOffset.UtcNow;
    }

    public void RejectTask(AgentTaskId taskId, string? feedback, VerificationRecord? verification)
    {
        var task = GetTask(taskId);
        if (task.Status is not AgentTaskStatus.AwaitingReview)
            throw new InvalidOperationException($"Task {taskId} is not awaiting review (status: {task.Status}).");

        ApplyReviewMetadata(task, feedback, verification);
        task.Status = AgentTaskStatus.Rejected;
        RefreshBusyStatus();
        LastActive = DateTimeOffset.UtcNow;
    }

    public void RefreshBusyStatus()
    {
        if (RunningTaskCount == 0)
            Status = AgentStatus.Active;
    }

    private static void ApplyReviewMetadata(AgentTaskInfo task, string? feedback, VerificationRecord? verification)
    {
        if (task.Proof is null)
            return;

        task.Proof.ReviewFeedback = feedback;
        task.Proof.ReviewedAt = DateTimeOffset.UtcNow;
        if (verification is not null)
            task.Proof.Verification = verification;
    }
}