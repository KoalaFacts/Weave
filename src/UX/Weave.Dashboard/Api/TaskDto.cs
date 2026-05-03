namespace Weave.Dashboard.Api;

public sealed record TaskDto
{
    public string TaskId { get; init; } = "";
    public string Description { get; init; } = "";
    public string Status { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}
