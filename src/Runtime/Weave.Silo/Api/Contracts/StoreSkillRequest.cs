namespace Weave.Silo.Api;

public sealed record StoreSkillRequest
{
    public required string Title { get; init; }
    public required string Description { get; init; }
    public List<string> Tags { get; init; } = [];
    public required List<SkillStepRequest> Steps { get; init; }
    public List<string> ToolsUsed { get; init; } = [];
    public required string CreatedByAgent { get; init; }
    public string? OriginTaskDescription { get; init; }
}
