using Microsoft.Extensions.Logging;
using Weave.Agents.Events;
using Weave.Agents.Models;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Actors;

public sealed class ChannelGatewayActor(
    IVirtualActorProvider actors,
    IEventBus eventBus,
    ICapabilityTokenService tokenService,
    ILogger<ChannelGatewayActor> logger,
    IActorState<ChannelGatewayState> persistentState) : IChannelGatewayActor
{
    private const string ChannelReceivePrefix = "channel:receive:";
    private const string ChannelSendPrefix = "channel:send:";

    private string? _key;

    public async Task OnActivatedAsync(string? key, CancellationToken cancellationToken)
    {
        _key = key;
        await persistentState.ReadStateAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(persistentState.State.WorkspaceId) && !string.IsNullOrWhiteSpace(key))
        {
            persistentState.State.WorkspaceId = key;
            await persistentState.WriteStateAsync(cancellationToken);
        }
    }

    public async Task RegisterChannelAsync(ChannelConfig config)
    {
        var channelKey = config.ChannelId.ToString();
        persistentState.State.Channels[channelKey] = config;

        EnsureWorkspaceId();
        await persistentState.WriteStateAsync();

        await eventBus.PublishAsync(new ChannelRegisteredEvent
        {
            SourceId = persistentState.State.WorkspaceId,
            WorkspaceId = WorkspaceId.From(persistentState.State.WorkspaceId),
            ChannelId = config.ChannelId,
            Type = config.Type,
            Name = config.Name
        }, CancellationToken.None);

        logger.LogInformation(
            "Channel {ChannelName} ({ChannelType}) registered in workspace {WorkspaceId}",
            config.Name,
            config.Type,
            persistentState.State.WorkspaceId);
    }

    public async Task UnregisterChannelAsync(ChannelId channelId)
    {
        var channelKey = channelId.ToString();
        persistentState.State.Channels.Remove(channelKey);
        await persistentState.WriteStateAsync();

        logger.LogInformation(
            "Channel {ChannelId} unregistered from workspace {WorkspaceId}",
            channelId,
            persistentState.State.WorkspaceId);
    }

    public async Task<OutboundMessage> RouteInboundAsync(InboundMessage message, CapabilityToken token)
    {
        var channelKey = message.ChannelId.ToString();

        Authorize(token, ChannelReceivePrefix + channelKey);
        Authorize(token, ChannelSendPrefix + channelKey);

        if (!persistentState.State.Channels.TryGetValue(channelKey, out var channel))
            throw new InvalidOperationException($"Channel {message.ChannelId} is not registered.");

        if (!channel.Enabled)
            throw new InvalidOperationException($"Channel {message.ChannelId} is disabled.");

        var agentName = ResolveAgentWithRules(channel, message);

        EnsureWorkspaceId();
        var workspaceId = WorkspaceId.From(persistentState.State.WorkspaceId);

        await eventBus.PublishAsync(new ChannelMessageReceivedEvent
        {
            SourceId = persistentState.State.WorkspaceId,
            WorkspaceId = workspaceId,
            ChannelId = message.ChannelId,
            SenderId = message.SenderId,
            AgentName = agentName
        }, CancellationToken.None);

        var agentActor = actors.GetActor<IAgentActor>(VirtualActorId.From($"{persistentState.State.WorkspaceId}/{agentName}"));
        var response = await agentActor.SendAsync(new AgentMessage
        {
            Role = "user",
            Content = message.Content,
            Metadata = message.Metadata,
            UserId = message.SenderId
        });

        var outbound = new OutboundMessage
        {
            ChannelId = message.ChannelId,
            Content = response.Content,
            ThreadId = message.ThreadId
        };

        await eventBus.PublishAsync(new ChannelMessageSentEvent
        {
            SourceId = persistentState.State.WorkspaceId,
            WorkspaceId = workspaceId,
            ChannelId = message.ChannelId,
            AgentName = agentName
        }, CancellationToken.None);

        logger.LogInformation(
            "Routed message from {SenderId} on channel {ChannelId} to agent {AgentName}",
            message.SenderId,
            message.ChannelId,
            agentName);

        return outbound;
    }

    public Task<IReadOnlyList<ChannelConfig>> GetChannelsAsync()
    {
        IReadOnlyList<ChannelConfig> channels = persistentState.State.Channels.Values.ToList();
        return Task.FromResult(channels);
    }

    public async Task SetRoutingRuleAsync(string pattern, string agentName)
    {
        persistentState.State.RoutingRules[pattern] = agentName;
        await persistentState.WriteStateAsync();

        logger.LogInformation(
            "Routing rule set: pattern '{Pattern}' -> agent '{AgentName}' in workspace {WorkspaceId}",
            pattern,
            agentName,
            persistentState.State.WorkspaceId);
    }

    internal string ResolveAgentWithRules(ChannelConfig channel, InboundMessage message)
    {
        if (!string.IsNullOrWhiteSpace(channel.TargetAgent))
            return channel.TargetAgent;

        foreach (var (pattern, agentName) in persistentState.State.RoutingRules)
        {
            if (message.SenderId.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
                message.Content.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
                return agentName;
        }

        throw new InvalidOperationException(
            $"No route found for message from {message.SenderId} on channel {message.ChannelId}.");
    }

    private void EnsureWorkspaceId()
    {
        if (!string.IsNullOrWhiteSpace(persistentState.State.WorkspaceId))
            return;

        var key = _key;
        if (!string.IsNullOrWhiteSpace(key))
            persistentState.State.WorkspaceId = key;
    }

    private void Authorize(CapabilityToken token, string grant)
    {
        if (!tokenService.Validate(token))
        {
            logger.LogWarning("Channel capability denied: invalid or expired token for grant '{Grant}' on workspace {WorkspaceId}",
                grant, persistentState.State.WorkspaceId);
            throw new UnauthorizedAccessException("Invalid or expired capability token");
        }

        var actorWorkspaceId = persistentState.State.WorkspaceId;
        if (!string.IsNullOrWhiteSpace(actorWorkspaceId)
            && !string.Equals(token.WorkspaceId, actorWorkspaceId, StringComparison.Ordinal))
        {
            logger.LogWarning("Channel capability denied: token workspace '{TokenWorkspaceId}' does not match actor workspace '{ActorWorkspaceId}'",
                token.WorkspaceId, actorWorkspaceId);
            throw new UnauthorizedAccessException(
                $"Token workspace '{token.WorkspaceId}' does not match actor workspace '{actorWorkspaceId}'");
        }

        if (!token.HasGrant(grant))
        {
            logger.LogWarning("Channel capability denied: token issued to '{IssuedTo}' does not grant '{Grant}'",
                token.IssuedTo, grant);
            throw new UnauthorizedAccessException($"Token does not grant '{grant}'");
        }
    }
}
