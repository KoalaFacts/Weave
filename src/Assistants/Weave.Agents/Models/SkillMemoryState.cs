using Weave.Shared.Ids;

namespace Weave.Agents.Models;

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
public sealed record SkillStep
{
    public required int Order { get; init; }
    public required string Action { get; init; }
    public string? ToolName { get; init; }
    public string? ExpectedOutcome { get; init; }
}
public sealed record SkillMemoryState
{
    public Dictionary<string, SkillDocument> Skills { get; init; } = [];
    public Dictionary<string, SkillSuggestion> SuggestedSkills { get; init; } = [];
    public string WorkspaceId { get; set; } = string.Empty;
}
public sealed record SkillSearchResult
{
    public required SkillDocument Skill { get; init; }
    public required double RelevanceScore { get; init; }
}

public sealed record SkillSuggestion
{
    public required SkillDocument Skill { get; init; }
    public string? SourceTaskId { get; init; }
    public DateTimeOffset SuggestedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record SkillSearchOptions
{
    public double MinSuccessRate { get; init; }
    public bool PreferRecent { get; init; }
}
