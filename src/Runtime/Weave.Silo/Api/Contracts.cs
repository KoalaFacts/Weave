using System.Text.Json.Serialization;
using Weave.Agents.Models;
using Weave.Tools.Models;
using Weave.Workspaces.Models;
using Weave.Workspaces.Plugins;

namespace Weave.Silo.Api;

// === Workspace Contracts ===

public sealed record StartWorkspaceRequest
{
    public required WorkspaceManifest Manifest { get; init; }
}

public sealed record WorkspaceResponse
{
    public required string WorkspaceId { get; init; }
    public string? Name { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter<WorkspaceStatus>))]
    public required WorkspaceStatus Status { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? StoppedAt { get; init; }
    public string? NetworkId { get; init; }
    public int ContainerCount { get; init; }
    public string? ErrorMessage { get; init; }

    public static WorkspaceResponse FromState(WorkspaceState state) => new()
    {
        WorkspaceId = state.WorkspaceId.ToString(),
        Name = state.Name,
        Status = state.Status,
        StartedAt = state.StartedAt,
        StoppedAt = state.StoppedAt,
        NetworkId = state.NetworkId?.ToString(),
        ContainerCount = state.Containers.Count,
        ErrorMessage = state.ErrorMessage
    };
}

// === Agent Contracts ===

public sealed record ActivateAgentRequest
{
    public required AgentDefinition Definition { get; init; }
}

public sealed record SubmitTaskRequest
{
    public required string Description { get; init; }
}

public sealed record SendMessageRequest
{
    public required string Content { get; init; }
    public string Role { get; init; } = "user";
}

public sealed record CompleteTaskRequest
{
    public required bool Success { get; init; }
    public required List<ProofItemRequest> Proof { get; init; }
}

public sealed record ReviewTaskRequest
{
    public required bool Accepted { get; init; }
    public string? Feedback { get; init; }
}

public sealed record ProofItemRequest
{
    [JsonConverter(typeof(JsonStringEnumConverter<ProofType>))]
    public required ProofType Type { get; init; }
    public required string Label { get; init; }
    public required string Value { get; init; }
    public string? Uri { get; init; }
}

public sealed record AgentResponse
{
    public required string AgentId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string AgentName { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter<AgentStatus>))]
    public required AgentStatus Status { get; init; }
    public string? Model { get; init; }
    public List<string> ConnectedTools { get; init; } = [];
    public List<TaskResponse> ActiveTasks { get; init; } = [];
    public DateTimeOffset? ActivatedAt { get; init; }
    public string? ErrorMessage { get; init; }

    public static AgentResponse FromState(AgentState state) => new()
    {
        AgentId = state.AgentId,
        WorkspaceId = state.WorkspaceId.ToString(),
        AgentName = state.AgentName,
        Status = state.Status,
        Model = state.Model,
        ConnectedTools = state.ConnectedTools,
        ActiveTasks = state.ActiveTasks.Select(TaskResponse.FromInfo).ToList(),
        ActivatedAt = state.ActivatedAt,
        ErrorMessage = state.ErrorMessage
    };
}

public sealed record TaskResponse
{
    public required string TaskId { get; init; }
    public required string Description { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter<AgentTaskStatus>))]
    public required AgentTaskStatus Status { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public ProofOfWorkResponse? Proof { get; init; }

    public static TaskResponse FromInfo(AgentTaskInfo info) => new()
    {
        TaskId = info.TaskId.ToString(),
        Description = info.Description,
        Status = info.Status,
        CreatedAt = info.CreatedAt,
        CompletedAt = info.CompletedAt,
        Proof = info.Proof is not null ? ProofOfWorkResponse.FromProof(info.Proof) : null
    };
}

public sealed record ProofOfWorkResponse
{
    public List<ProofItemResponse> Items { get; init; } = [];
    public DateTimeOffset SubmittedAt { get; init; }
    public string? ReviewFeedback { get; init; }
    public DateTimeOffset? ReviewedAt { get; init; }
    public VerificationRecordResponse? Verification { get; init; }

    public static ProofOfWorkResponse FromProof(ProofOfWork proof) => new()
    {
        Items = proof.Items.Select(ProofItemResponse.FromItem).ToList(),
        SubmittedAt = proof.SubmittedAt,
        ReviewFeedback = proof.ReviewFeedback,
        ReviewedAt = proof.ReviewedAt,
        Verification = proof.Verification is not null
            ? VerificationRecordResponse.FromRecord(proof.Verification)
            : null
    };
}

public sealed record VerificationRecordResponse
{
    public List<VerificationVoteResponse> Votes { get; init; } = [];
    public required int RequiredVotes { get; init; }
    public required bool ConsensusReached { get; init; }
    public required bool Accepted { get; init; }
    public DateTimeOffset CompletedAt { get; init; }

    public static VerificationRecordResponse FromRecord(VerificationRecord record) => new()
    {
        Votes = record.Votes.Select(VerificationVoteResponse.FromVote).ToList(),
        RequiredVotes = record.RequiredVotes,
        ConsensusReached = record.ConsensusReached,
        Accepted = record.Accepted,
        CompletedAt = record.CompletedAt
    };
}

public sealed record VerificationVoteResponse
{
    public required string ValidatorId { get; init; }
    public required bool Accepted { get; init; }
    public required string Reason { get; init; }
    public DateTimeOffset VotedAt { get; init; }
    public List<ConditionResultResponse> ConditionResults { get; init; } = [];

    public static VerificationVoteResponse FromVote(VerificationVote vote) => new()
    {
        ValidatorId = vote.ValidatorId,
        Accepted = vote.Accepted,
        Reason = vote.Reason,
        VotedAt = vote.VotedAt,
        ConditionResults = vote.ConditionResults.Select(ConditionResultResponse.FromResult).ToList()
    };
}

public sealed record ConditionResultResponse
{
    public required string ConditionName { get; init; }
    public required bool Passed { get; init; }
    public string? Detail { get; init; }

    public static ConditionResultResponse FromResult(ConditionResult result) => new()
    {
        ConditionName = result.ConditionName,
        Passed = result.Passed,
        Detail = result.Detail
    };
}

public sealed record ProofItemResponse
{
    [JsonConverter(typeof(JsonStringEnumConverter<ProofType>))]
    public required ProofType Type { get; init; }
    public required string Label { get; init; }
    public required string Value { get; init; }
    public string? Uri { get; init; }

    public static ProofItemResponse FromItem(ProofItem item) => new()
    {
        Type = item.Type,
        Label = item.Label,
        Value = item.Value,
        Uri = item.Uri
    };
}

public sealed record AgentChatResponse
{
    public required string Content { get; init; }
    public required string ConversationId { get; init; }
    public List<ConversationMessageResponse> Messages { get; init; } = [];
    public bool UsedTools { get; init; }
    public string? Model { get; init; }

    public static AgentChatResponse FromResponse(Weave.Agents.Models.AgentChatResponse response) => new()
    {
        Content = response.Content,
        ConversationId = response.ConversationId,
        UsedTools = response.UsedTools,
        Model = response.Model,
        Messages = response.Messages.Select(ConversationMessageResponse.FromMessage).ToList()
    };
}

public sealed record ConversationMessageResponse
{
    public required string Role { get; init; }
    public required string Content { get; init; }
    public DateTimeOffset Timestamp { get; init; }

    public static ConversationMessageResponse FromMessage(ConversationMessage message) => new()
    {
        Role = message.Role,
        Content = message.Content,
        Timestamp = message.Timestamp
    };
}

// === Plugin Contracts ===

public sealed record ConnectPluginRequest
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public string? Description { get; init; }
    public Dictionary<string, string>? Config { get; init; }
}

public sealed record ConnectPluginResponse
{
    public required PluginStatus Status { get; init; }
    public List<string> Warnings { get; init; } = [];
}

// === Tool Contracts ===

public sealed record ToolConnectionResponse
{
    public required string ToolName { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter<ToolType>))]
    public required ToolType ToolType { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter<ToolConnectionStatus>))]
    public required ToolConnectionStatus Status { get; init; }
    public string? Endpoint { get; init; }
    public DateTimeOffset? ConnectedAt { get; init; }
    public string? ErrorMessage { get; init; }

    public static ToolConnectionResponse FromConnection(ToolConnection conn) => new()
    {
        ToolName = conn.ToolName,
        ToolType = Enum.Parse<ToolType>(conn.ToolType, ignoreCase: true),
        Status = conn.Status,
        Endpoint = conn.Endpoint,
        ConnectedAt = conn.ConnectedAt,
        ErrorMessage = conn.ErrorMessage
    };
}
