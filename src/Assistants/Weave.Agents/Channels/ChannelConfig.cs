using Weave.Shared.Ids;

namespace Weave.Agents.Models;

public sealed record ChannelConfig
{
    public required ChannelId ChannelId { get; init; }
    public required ChannelType Type { get; init; }
    public required string Name { get; init; }
    public Dictionary<string, string> Config { get; init; } = [];
    public string? TargetAgent { get; init; }
    public bool Enabled { get; init; } = true;
}
