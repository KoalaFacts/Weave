namespace Weave.Agents.Models;

public sealed record SkillMemoryState
{
    public Dictionary<string, SkillDocument> Skills { get; init; } = [];
    public Dictionary<string, SkillSuggestion> SuggestedSkills { get; init; } = [];
    public string WorkspaceId { get; set; } = string.Empty;
}
