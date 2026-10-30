namespace Weave.Agents.Channels;

public sealed record ChannelGatewayState
{
    public Dictionary<string, ChannelConfig> Channels { get; init; } = [];
    public Dictionary<string, string> RoutingRules { get; init; } = [];
    public string WorkspaceId { get; set; } = string.Empty;

    /// <summary>
    /// Resolves the agent name to route an inbound message to: the channel's
    /// explicit <c>TargetAgent</c> wins; otherwise the message is matched
    /// against <see cref="RoutingRules"/> by sender-id substring or content
    /// prefix. Throws when no rule matches. Multi-step pattern matching lives
    /// here so the actor stays a thin orchestrator.
    /// </summary>
    public string ResolveAgent(ChannelConfig channel, InboundMessage message)
    {
        if (!string.IsNullOrWhiteSpace(channel.TargetAgent))
            return channel.TargetAgent;

        foreach (var (pattern, agentName) in RoutingRules)
        {
            if (message.SenderId.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
                message.Content.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
                return agentName;
        }

        throw new InvalidOperationException(
            $"No route found for message from {message.SenderId} on channel {message.ChannelId}.");
    }
}
