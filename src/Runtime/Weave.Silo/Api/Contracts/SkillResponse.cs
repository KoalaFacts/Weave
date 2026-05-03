using Weave.Agents.Models;

namespace Weave.Silo.Api;

public sealed record SkillResponse
{
    public required string SkillId { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public List<string> Tags { get; init; } = [];
    public List<string> ToolsUsed { get; init; } = [];
    public required string CreatedByAgent { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public int UseCount { get; init; }
    public double SuccessRate { get; init; }
    public string? OriginTaskDescription { get; init; }
    public DateTimeOffset? ArchivedAt { get; init; }

    public static SkillResponse FromDocument(SkillDocument doc) => new()
    {
        SkillId = doc.SkillId.ToString(),
        Title = doc.Title,
        Description = doc.Description,
        Tags = [.. doc.Tags],
        ToolsUsed = [.. doc.ToolsUsed],
        CreatedByAgent = doc.CreatedByAgent,
        CreatedAt = doc.CreatedAt,
        UseCount = doc.UseCount,
        SuccessRate = doc.SuccessRate,
        OriginTaskDescription = doc.OriginTaskDescription,
        ArchivedAt = doc.ArchivedAt
    };
}
