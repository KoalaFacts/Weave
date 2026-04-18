using Weave.Shared.Ids;

namespace Weave.Agents.Models;

[GenerateSerializer]
public sealed record SkillDocument
{
    [Id(0)] public required SkillId SkillId { get; init; }
    [Id(1)] public required string Title { get; init; }
    [Id(2)] public required string Description { get; init; }
    [Id(3)] public required List<string> Tags { get; init; }
    [Id(4)] public required List<SkillStep> Steps { get; init; }
    [Id(5)] public required List<string> ToolsUsed { get; init; }
    [Id(6)] public required string CreatedByAgent { get; init; }
    [Id(7)] public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    [Id(8)] public int UseCount { get; set; }
    [Id(9)] public DateTimeOffset? LastUsedAt { get; set; }
    [Id(10)] public double SuccessRate { get; set; } = 1.0;
    [Id(11)] public string? OriginTaskDescription { get; init; }
}

[GenerateSerializer]
public sealed record SkillStep
{
    [Id(0)] public required int Order { get; init; }
    [Id(1)] public required string Action { get; init; }
    [Id(2)] public string? ToolName { get; init; }
    [Id(3)] public string? ExpectedOutcome { get; init; }
}

[GenerateSerializer]
public sealed record SkillMemoryState
{
    [Id(0)] public Dictionary<string, SkillDocument> Skills { get; init; } = [];
    [Id(1)] public string WorkspaceId { get; set; } = string.Empty;
}

[GenerateSerializer]
public sealed record SkillSearchResult
{
    [Id(0)] public required SkillDocument Skill { get; init; }
    [Id(1)] public required double RelevanceScore { get; init; }
}
