namespace Weave.Agents.Skills;

public sealed record SkillSuggestion
{
    public required SkillDocument Skill { get; init; }
    public string? SourceTaskId { get; init; }
    public DateTimeOffset SuggestedAt { get; init; } = DateTimeOffset.UtcNow;
}
