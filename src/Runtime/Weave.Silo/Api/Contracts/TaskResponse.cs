using System.Text.Json.Serialization;
using Weave.Agents.Models;

namespace Weave.Silo.Api;

public sealed record TaskResponse
{
    public required string TaskId { get; init; }
    public required string Description { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter<AgentTaskStatus>))]
    public required AgentTaskStatus Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
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
