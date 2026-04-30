using Weave.Agents.Models;

namespace Weave.Silo.Api;

public sealed record SkillSuggestionResponse
{
    public required SkillResponse Skill { get; init; }
    public string? SourceTaskId { get; init; }
    public required DateTimeOffset SuggestedAt { get; init; }

    public static SkillSuggestionResponse FromSuggestion(SkillSuggestion suggestion) => new()
    {
        Skill = SkillResponse.FromDocument(suggestion.Skill),
        SourceTaskId = suggestion.SourceTaskId,
        SuggestedAt = suggestion.SuggestedAt
    };
}
