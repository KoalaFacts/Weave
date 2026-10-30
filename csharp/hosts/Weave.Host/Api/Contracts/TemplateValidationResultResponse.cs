namespace Weave.Silo.Api;

public sealed record TemplateValidationResultResponse
{
    public required string Check { get; init; }
    public required bool Passed { get; init; }
    public string? Detail { get; init; }
}
