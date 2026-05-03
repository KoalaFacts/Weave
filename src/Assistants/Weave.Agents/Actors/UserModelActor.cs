using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Weave.Agents.Events;
using Weave.Agents.Models;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Actors;

public sealed class UserModelActor(
    IEventBus eventBus,
    TimeProvider timeProvider,
    ICapabilityTokenService tokenService,
    ILogger<UserModelActor> logger,
    IActorState<UserProfileState> persistentState) : IUserModelActor
{
    private string? _key;

    public async Task OnActivatedAsync(string? key, CancellationToken cancellationToken)
    {
        _key = key;
        await persistentState.ReadStateAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(persistentState.State.UserId))
        {
            ApplyIdentity(key);
            await persistentState.WriteStateAsync(cancellationToken);
        }
    }

    public async Task RecordInteractionAsync(InteractionRecord record, CapabilityToken token)
    {
        EnsureIdentity();
        Authorize(token, write: true);

        if (persistentState.State.RecentInteractions.Count >= persistentState.State.MaxRecentInteractions)
            persistentState.State.RecentInteractions.RemoveAt(0);

        persistentState.State.RecentInteractions.Add(record);

        foreach (var topic in record.Topics)
        {
            if (persistentState.State.TopicFrequency.TryGetValue(topic, out var count))
                persistentState.State.TopicFrequency[topic] = count + 1;
            else
                persistentState.State.TopicFrequency[topic] = 1;
        }

        persistentState.State.TotalInteractions++;
        var now = timeProvider.GetUtcNow();
        persistentState.State.FirstSeenAt ??= now;
        persistentState.State.LastSeenAt = now;

        await persistentState.WriteStateAsync(token.CancellationToken);

        await eventBus.PublishAsync(new UserInteractionRecordedEvent
        {
            SourceId = $"{persistentState.State.WorkspaceId}/{persistentState.State.UserId}",
            WorkspaceId = WorkspaceId.From(persistentState.State.WorkspaceId),
            UserId = persistentState.State.UserId,
            AgentName = record.AgentName
        }, token.CancellationToken);

        logger.LogInformation(
            "Recorded interaction for user {UserId} with agent {AgentName}",
            persistentState.State.UserId,
            record.AgentName);
    }

    public async Task SetPreferenceAsync(string key, string value, CapabilityToken token)
    {
        EnsureIdentity();
        Authorize(token, write: true);
        persistentState.State.Preferences[key] = value;
        await persistentState.WriteStateAsync(token.CancellationToken);
    }

    public async Task SetDomainContextAsync(string key, string value, CapabilityToken token)
    {
        EnsureIdentity();
        Authorize(token, write: true);
        persistentState.State.DomainContext[key] = value;
        await persistentState.WriteStateAsync(token.CancellationToken);
    }

    public Task<UserProfileState> GetProfileAsync(CapabilityToken token)
    {
        EnsureIdentity();
        Authorize(token, write: false);
        return Task.FromResult(persistentState.State);
    }

    public Task<string> GetContextSummaryAsync(CapabilityToken token)
    {
        EnsureIdentity();
        Authorize(token, write: false);
        if (persistentState.State.TotalInteractions == 0
            && persistentState.State.Preferences.Count == 0
            && persistentState.State.DomainContext.Count == 0)
        {
            return Task.FromResult(string.Empty);
        }

        var sb = new StringBuilder();

        if (persistentState.State.Preferences.Count > 0)
        {
            sb.Append("User preferences: ");
            sb.Append(string.Join(", ", persistentState.State.Preferences.Select(kv => $"{kv.Key}={kv.Value}")));
            sb.Append(". ");
        }

        if (persistentState.State.TopicFrequency.Count > 0)
        {
            var topTopics = persistentState.State.TopicFrequency
                .OrderByDescending(kv => kv.Value)
                .Take(5)
                .Select(kv => $"{kv.Key} ({kv.Value})");

            sb.Append("Top topics: ");
            sb.Append(string.Join(", ", topTopics));
            sb.Append(". ");
        }

        if (persistentState.State.DomainContext.Count > 0)
        {
            sb.Append("Domain context: ");
            sb.Append(string.Join(", ", persistentState.State.DomainContext.Select(kv => $"{kv.Key}={kv.Value}")));
            sb.Append(". ");
        }

        sb.Append(CultureInfo.InvariantCulture, $"Interactions: {persistentState.State.TotalInteractions} total.");

        return Task.FromResult(sb.ToString());
    }

    public async Task ClearAsync(CapabilityToken token)
    {
        EnsureIdentity();
        Authorize(token, write: true);
        var state = persistentState.State;
        state.Preferences.Clear();
        state.RecentInteractions.Clear();
        state.TopicFrequency.Clear();
        state.DomainContext.Clear();
        state.TotalInteractions = 0;
        state.FirstSeenAt = null;
        state.LastSeenAt = null;
        state.PreferredModel = null;
        state.PreferredLanguage = null;
        state.MaxRecentInteractions = 100;

        await persistentState.WriteStateAsync(token.CancellationToken);
    }

    private void EnsureIdentity()
    {
        if (!string.IsNullOrWhiteSpace(persistentState.State.UserId))
            return;

        ApplyIdentity(_key);
    }

    private void ApplyIdentity(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        var parts = key.Split('/', 2);
        persistentState.State.WorkspaceId = parts.Length > 1 ? parts[0] : key;
        persistentState.State.UserId = parts.Length > 1 ? parts[1] : key;
    }

    private void Authorize(CapabilityToken token, bool write)
    {
        var verb = write ? "write" : "read";
        var grant = $"user:{verb}:{persistentState.State.UserId}";

        if (!tokenService.Validate(token))
        {
            logger.LogWarning("User capability denied: invalid or expired token for grant '{Grant}' on workspace {WorkspaceId}",
                grant, persistentState.State.WorkspaceId);
            throw new UnauthorizedAccessException("Invalid or expired capability token");
        }

        var actorWorkspaceId = persistentState.State.WorkspaceId;
        if (!string.IsNullOrWhiteSpace(actorWorkspaceId)
            && !string.Equals(token.WorkspaceId, actorWorkspaceId, StringComparison.Ordinal))
        {
            logger.LogWarning("User capability denied: token workspace '{TokenWorkspaceId}' does not match actor workspace '{ActorWorkspaceId}'",
                token.WorkspaceId, actorWorkspaceId);
            throw new UnauthorizedAccessException(
                $"Token workspace '{token.WorkspaceId}' does not match actor workspace '{actorWorkspaceId}'");
        }

        if (!token.HasGrant(grant))
        {
            logger.LogWarning("User capability denied: token issued to '{IssuedTo}' does not grant '{Grant}'",
                token.IssuedTo, grant);
            throw new UnauthorizedAccessException($"Token does not grant '{grant}'");
        }
    }
}
