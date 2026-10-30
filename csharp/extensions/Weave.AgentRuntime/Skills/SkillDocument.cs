using Weave.Shared.Ids;

namespace Weave.Agents.Skills;

public sealed record SkillDocument
{
    public required SkillId SkillId { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required List<string> Tags { get; init; }
    public required List<SkillStep> Steps { get; init; }
    public required List<string> ToolsUsed { get; init; }
    public required string CreatedByAgent { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public int UseCount { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public double SuccessRate { get; set; } = 1.0;
    public string? OriginTaskDescription { get; init; }
    public DateTimeOffset? ArchivedAt { get; set; }
}
