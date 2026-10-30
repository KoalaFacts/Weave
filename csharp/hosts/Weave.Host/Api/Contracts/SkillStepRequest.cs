namespace Weave.Silo.Api;

public sealed record SkillStepRequest
{
    public required string Action { get; init; }
    public string? ToolName { get; init; }
    public string? ExpectedOutcome { get; init; }
}
