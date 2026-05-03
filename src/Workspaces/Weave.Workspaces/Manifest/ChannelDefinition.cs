namespace Weave.Workspaces.Models;

public sealed record ChannelDefinition
{
    public required string Type { get; init; }
    public string? TargetAgent { get; init; }
    public Dictionary<string, string> Config { get; init; } = [];
    public bool Enabled { get; init; } = true;
}
