using Weave.Shared.Ids;

namespace Weave.Workspaces.Models;

public sealed record CapabilityTemplate
{
    public required TemplateId TemplateId { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Version { get; init; }
    public required string Author { get; init; }
    public TemplateStatus Status { get; set; } = TemplateStatus.Draft;
    public required AgentDefinition AgentDefinition { get; init; }
    public Dictionary<string, ToolDefinition> RequiredTools { get; init; } = [];
    public List<string> RequiredCapabilities { get; init; } = [];
    public List<TemplateValidationResult> ValidationResults { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PublishedAt { get; set; }
    public List<string> Tags { get; init; } = [];
    public int InstantiationCount { get; set; }
    public Dictionary<string, string> DefaultParameters { get; init; } = [];
}
