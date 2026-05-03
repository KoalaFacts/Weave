namespace Weave.Agents.Models;

public sealed record SkillStep
{
    public required int Order { get; init; }
    public required string Action { get; init; }
    public string? ToolName { get; init; }
    public string? ExpectedOutcome { get; init; }
}
