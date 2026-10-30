namespace Weave.Agents.Skills;

public sealed record SkillSearchResult
{
    public required SkillDocument Skill { get; init; }
    public required double RelevanceScore { get; init; }
}
