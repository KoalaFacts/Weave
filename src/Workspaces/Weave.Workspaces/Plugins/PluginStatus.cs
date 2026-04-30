namespace Weave.Workspaces.Plugins;

public sealed record PluginStatus
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public bool IsConnected { get; init; }
    public string? Error { get; init; }
    public IReadOnlyDictionary<string, string> Info { get; init; } = new Dictionary<string, string>();
}