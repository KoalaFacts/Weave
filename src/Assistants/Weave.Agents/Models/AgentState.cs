using Weave.Shared.Ids;
using Weave.Workspaces.Models;

namespace Weave.Agents.Models;

[GenerateSerializer]
public sealed record AgentState
{
    [Id(0)] public string AgentId { get; set; } = string.Empty;
    [Id(1)] public WorkspaceId WorkspaceId { get; set; } = WorkspaceId.Empty;
    [Id(2)] public string AgentName { get; set; } = string.Empty;
    [Id(3)] public AgentStatus Status { get; set; } = AgentStatus.Idle;
    [Id(4)] public string? Model { get; set; }
    [Id(5)] public List<string> ConnectedTools { get; init; } = [];
    [Id(6)] public List<AgentTaskInfo> ActiveTasks { get; init; } = [];
    [Id(7)] public int MaxConcurrentTasks { get; set; } = 1;
    [Id(8)] public DateTimeOffset? ActivatedAt { get; set; }
    [Id(9)] public DateTimeOffset? DeactivatedAt { get; set; }
    [Id(10)] public string? ErrorMessage { get; set; }
    [Id(11)] public List<ConversationMessage> History { get; init; } = [];
    [Id(12)] public DateTimeOffset? LastActive { get; set; }
    [Id(13)] public int TotalTasksCompleted { get; set; }
    [Id(14)] public AgentDefinition? Definition { get; set; }
    [Id(15)] public string? ConversationId { get; set; }

    public int RunningTaskCount =>
        ActiveTasks.Count(task => task.Status is AgentTaskStatus.Running);

    public AgentTaskInfo GetTask(AgentTaskId taskId)
    {
        foreach (var task in ActiveTasks)
        {
            if (task.TaskId == taskId)
                return task;
        }
        throw new InvalidOperationException($"Task {taskId} not found.");
    }

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

[GenerateSerializer]
public sealed record AgentTaskInfo
{
    [Id(0)] public required AgentTaskId TaskId { get; init; }
    [Id(1)] public required string Description { get; init; }
    [Id(2)] public AgentTaskStatus Status { get; set; } = AgentTaskStatus.Pending;
    [Id(3)] public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    [Id(4)] public DateTimeOffset? CompletedAt { get; set; }
    [Id(5)] public string? ResultSummary { get; set; }
    [Id(6)] public ProofOfWork? Proof { get; set; }
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

[GenerateSerializer]
public sealed record ProofOfWork
{
    [Id(0)] public List<ProofItem> Items { get; init; } = [];
    [Id(1)] public DateTimeOffset SubmittedAt { get; init; } = DateTimeOffset.UtcNow;
    [Id(2)] public string? ReviewFeedback { get; set; }
    [Id(3)] public DateTimeOffset? ReviewedAt { get; set; }
    [Id(4)] public VerificationRecord? Verification { get; set; }
}

[GenerateSerializer]
public sealed record ProofItem
{
    [Id(0)] public required ProofType Type { get; init; }
    [Id(1)] public required string Label { get; init; }
    [Id(2)] public required string Value { get; init; }
    [Id(3)] public string? Uri { get; init; }
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

[GenerateSerializer]
public sealed record VerificationCondition
{
    [Id(0)] public required string Name { get; init; }
    [Id(1)] public required string Description { get; init; }
}

[GenerateSerializer]
public sealed record VerificationVote
{
    [Id(0)] public required string ValidatorId { get; init; }
    [Id(1)] public required bool Accepted { get; init; }
    [Id(2)] public required string Reason { get; init; }
    [Id(3)] public DateTimeOffset VotedAt { get; init; } = DateTimeOffset.UtcNow;
    [Id(4)] public List<ConditionResult> ConditionResults { get; init; } = [];
}

[GenerateSerializer]
public sealed record ConditionResult
{
    [Id(0)] public required string ConditionName { get; init; }
    [Id(1)] public required bool Passed { get; init; }
    [Id(2)] public string? Detail { get; init; }
}

[GenerateSerializer]
public sealed record VerificationRecord
{
    [Id(0)] public List<VerificationVote> Votes { get; init; } = [];
    [Id(1)] public required int RequiredVotes { get; init; }
    [Id(2)] public required bool ConsensusReached { get; init; }
    [Id(3)] public required bool Accepted { get; init; }
    [Id(4)] public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.UtcNow;
}

[GenerateSerializer]
public sealed record ValidatorConfig
{
    [Id(0)] public string? ModelId { get; init; }
}

[GenerateSerializer]
public sealed record VerifierState
{
    [Id(0)] public List<VerificationCondition> Conditions { get; init; } = [];
    [Id(1)] public int RequiredValidators { get; set; } = 2;
    [Id(2)] public List<ValidatorConfig> ValidatorConfigs { get; init; } = [];
}

[GenerateSerializer]
public sealed record ConversationMessage
{
    [Id(0)] public required string Role { get; init; }
    [Id(1)] public required string Content { get; init; }
    [Id(2)] public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
