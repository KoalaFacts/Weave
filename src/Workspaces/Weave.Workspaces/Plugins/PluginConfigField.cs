namespace Weave.Workspaces.Plugins;

public sealed record PluginConfigField
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public bool Required { get; init; }
    public bool Secret { get; init; }
    public string? Default { get; init; }
    public string? EnvVar { get; init; }
}