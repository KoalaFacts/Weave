namespace Weave.Workspaces.Templates;

public sealed record TemplateValidationResult
{
    public required string Check { get; init; }
    public required bool Passed { get; init; }
    public string? Detail { get; init; }
}
