namespace Weave.Silo.Api;

public sealed record SubmitTaskRequest
{
    public required string Description { get; init; }
}
