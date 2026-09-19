using Weave.Workspaces.Manifest;

namespace Weave.Actions.Marketplace;

/// <summary>
/// Template payload returned with a marketplace install — preserves the
/// agent + tool definitions needed for downstream workspace scaffolding.
/// </summary>
public sealed record InstalledMarketplaceTemplate
{
    public required string TemplateId { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Version { get; init; }
    public required string Author { get; init; }
    public required string Status { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public int InstantiationCount { get; init; }
    public required AgentDefinition AgentDefinition { get; init; }
    public IReadOnlyDictionary<string, ToolDefinition> RequiredTools { get; init; }
        = new Dictionary<string, ToolDefinition>();
}
