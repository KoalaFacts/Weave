namespace Weave.Dashboard.Api;

public sealed record WorkspaceDto
{
    public string WorkspaceId { get; init; } = "";
    public string? Name { get; init; }
    public string Status { get; init; } = "";
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? StoppedAt { get; init; }
    public string? NetworkId { get; init; }
    public int ContainerCount { get; init; }
    public string? ErrorMessage { get; init; }
}
