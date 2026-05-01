namespace Weave.Agents.Models;

public sealed record SkillSearchOptions
{
    public double MinSuccessRate { get; init; }
    public bool PreferRecent { get; init; }
}
