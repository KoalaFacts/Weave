namespace Weave.Silo.Api;

public sealed record ReviewTaskRequest
{
    public required bool Accepted { get; init; }
    public string? Feedback { get; init; }
}
