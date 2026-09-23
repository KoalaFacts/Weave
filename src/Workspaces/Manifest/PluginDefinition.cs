namespace Weave.Workspaces.Manifest;

public sealed record PluginDefinition
{
    public required string Type { get; init; }
    public string? Description { get; init; }
    public Dictionary<string, string> Config { get; init; } = [];
    public string? EnabledWhen { get; init; }
}
