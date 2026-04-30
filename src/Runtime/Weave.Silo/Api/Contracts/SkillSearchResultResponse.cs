using Weave.Agents.Models;

namespace Weave.Silo.Api;

public sealed record SkillSearchResultResponse
{
    public required SkillResponse Skill { get; init; }
    public required double RelevanceScore { get; init; }

    public static SkillSearchResultResponse FromResult(SkillSearchResult result) => new()
    {
        Skill = SkillResponse.FromDocument(result.Skill),
        RelevanceScore = result.RelevanceScore
    };
}
