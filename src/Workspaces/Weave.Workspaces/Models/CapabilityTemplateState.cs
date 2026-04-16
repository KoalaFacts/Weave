using Weave.Shared.Ids;

namespace Weave.Workspaces.Models;

public enum TemplateStatus { Draft, Published, Deprecated }

[GenerateSerializer]
public sealed record CapabilityTemplate
{
    [Id(0)] public required TemplateId TemplateId { get; init; }
    [Id(1)] public required string Name { get; init; }
    [Id(2)] public required string Description { get; init; }
    [Id(3)] public required string Version { get; init; }
    [Id(4)] public required string Author { get; init; }
    [Id(5)] public TemplateStatus Status { get; set; } = TemplateStatus.Draft;
    [Id(6)] public required AgentDefinition AgentDefinition { get; init; }
    [Id(7)] public Dictionary<string, ToolDefinition> RequiredTools { get; init; } = [];
    [Id(8)] public List<string> RequiredCapabilities { get; init; } = [];
    [Id(9)] public List<TemplateValidationResult> ValidationResults { get; init; } = [];
    [Id(10)] public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    [Id(11)] public DateTimeOffset? PublishedAt { get; set; }
    [Id(12)] public List<string> Tags { get; init; } = [];
    [Id(13)] public int InstantiationCount { get; set; }
    [Id(14)] public Dictionary<string, string> DefaultParameters { get; init; } = [];
}

[GenerateSerializer]
public sealed record TemplateValidationResult
{
    [Id(0)] public required string Check { get; init; }
    [Id(1)] public required bool Passed { get; init; }
    [Id(2)] public string? Detail { get; init; }
}

[GenerateSerializer]
public sealed record TemplateRegistryState
{
    [Id(0)] public Dictionary<string, CapabilityTemplate> Templates { get; init; } = [];
}
