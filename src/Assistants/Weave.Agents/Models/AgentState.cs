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

public enum AgentStatus
{
    Idle,
    Activating,
    Active,
    Busy,
    Deactivating,
    Error
}
public sealed record AgentTaskInfo
{
    public required AgentTaskId TaskId { get; init; }
    public required string Description { get; init; }
    public AgentTaskStatus Status { get; set; } = AgentTaskStatus.Pending;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ResultSummary { get; set; }
    public ProofOfWork? Proof { get; set; }
}

public enum AgentTaskStatus
{
    Pending,
    Running,
    AwaitingReview,
    Accepted,
    Rejected,
    Completed,
    Failed,
    Cancelled
}
public sealed record ProofOfWork
{
    public List<ProofItem> Items { get; init; } = [];
    public DateTimeOffset SubmittedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? ReviewFeedback { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public VerificationRecord? Verification { get; set; }
}
public sealed record ProofItem
{
    public required ProofType Type { get; init; }
    public required string Label { get; init; }
    public required string Value { get; init; }
    public string? Uri { get; init; }
}

public enum ProofType
{
    CiStatus,
    TestResults,
    PullRequest,
    CodeReview,
    DiffSummary,
    Custom
}
public sealed record VerificationCondition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
}
public sealed record VerificationVote
{
    public required string ValidatorId { get; init; }
    public required bool Accepted { get; init; }
    public required string Reason { get; init; }
    public DateTimeOffset VotedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<ConditionResult> ConditionResults { get; init; } = [];
}
public sealed record ConditionResult
{
    public required string ConditionName { get; init; }
    public required bool Passed { get; init; }
    public string? Detail { get; init; }
}
public sealed record VerificationRecord
{
    public List<VerificationVote> Votes { get; init; } = [];
    public required int RequiredVotes { get; init; }
    public required bool ConsensusReached { get; init; }
    public required bool Accepted { get; init; }
    public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.UtcNow;
}
public sealed record ValidatorConfig
{
    public string? ModelId { get; init; }
}
public sealed record VerifierState
{
    public List<VerificationCondition> Conditions { get; init; } = [];
    public int RequiredValidators { get; set; } = 2;
    public List<ValidatorConfig> ValidatorConfigs { get; init; } = [];
}
public sealed record ConversationMessage
{
    public required string Role { get; init; }
    public required string Content { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
