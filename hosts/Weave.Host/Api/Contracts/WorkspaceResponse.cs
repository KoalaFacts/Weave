using System.Text.Json.Serialization;
using Weave.Workspaces.Lifecycle;
namespace Weave.Silo.Api;

public sealed record WorkspaceResponse
{
    public required string WorkspaceId { get; init; }
    public string? Name { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter<WorkspaceStatus>))]
    public required WorkspaceStatus Status { get; init; }
    public required int ContainerCount { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? StoppedAt { get; init; }
    public string? NetworkId { get; init; }
    public string? ErrorMessage { get; init; }

    public static WorkspaceResponse FromState(WorkspaceState state) => new()
    {
        WorkspaceId = state.WorkspaceId.ToString(),
        Name = state.Name,
        Status = state.Status,
        ContainerCount = state.Containers.Count,
        StartedAt = state.StartedAt,
        StoppedAt = state.StoppedAt,
        NetworkId = state.NetworkId?.ToString(),
        ErrorMessage = state.ErrorMessage
    };
}
