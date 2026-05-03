namespace Weave.Agents.Models;

public sealed record ChannelGatewayState
{
    public Dictionary<string, ChannelConfig> Channels { get; init; } = [];
    public Dictionary<string, string> RoutingRules { get; init; } = [];
    public string WorkspaceId { get; set; } = string.Empty;
}
