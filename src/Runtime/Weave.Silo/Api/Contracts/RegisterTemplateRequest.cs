using Weave.Workspaces.Models;

namespace Weave.Silo.Api;

public sealed record RegisterTemplateRequest
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Version { get; init; }
    public required string Author { get; init; }
    public required AgentDefinition AgentDefinition { get; init; }
    public Dictionary<string, ToolDefinition>? RequiredTools { get; init; }
    public List<string>? RequiredCapabilities { get; init; }
    public List<string>? Tags { get; init; }
    public Dictionary<string, string>? DefaultParameters { get; init; }
}
