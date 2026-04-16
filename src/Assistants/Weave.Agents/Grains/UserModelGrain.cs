using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Weave.Agents.Events;
using Weave.Agents.Models;
using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Grains;

public sealed class UserModelGrain(
    IEventBus eventBus,
    ILogger<UserModelGrain> logger,
    [PersistentState("user-model", "Default")] IPersistentState<UserProfileState> persistentState) : Grain, IUserModelGrain
{
    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await persistentState.ReadStateAsync(cancellationToken);

        var key = TryGetPrimaryKeyString();
        if (string.IsNullOrWhiteSpace(persistentState.State.UserId))
        {
            ApplyIdentity(key);
            await persistentState.WriteStateAsync(cancellationToken);
        }
    }

    public async Task RecordInteractionAsync(InteractionRecord record)
    {
        EnsureIdentity();

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
        persistentState.State.FirstSeenAt ??= DateTimeOffset.UtcNow;
        persistentState.State.LastSeenAt = DateTimeOffset.UtcNow;

        await persistentState.WriteStateAsync();

        await eventBus.PublishAsync(new UserInteractionRecordedEvent
        {
            SourceId = $"{persistentState.State.WorkspaceId}/{persistentState.State.UserId}",
            WorkspaceId = WorkspaceId.From(persistentState.State.WorkspaceId),
            UserId = persistentState.State.UserId,
            AgentName = record.AgentName
        }, CancellationToken.None);

        logger.LogInformation(
            "Recorded interaction for user {UserId} with agent {AgentName}",
            persistentState.State.UserId,
            record.AgentName);
    }

    public async Task SetPreferenceAsync(string key, string value)
    {
        EnsureIdentity();
        persistentState.State.Preferences[key] = value;
        await persistentState.WriteStateAsync();
    }

    public async Task SetDomainContextAsync(string key, string value)
    {
        EnsureIdentity();
        persistentState.State.DomainContext[key] = value;
        await persistentState.WriteStateAsync();
    }

    public Task<UserProfileState> GetProfileAsync() => Task.FromResult(persistentState.State);

    public Task<string> GetContextSummaryAsync()
    {
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

    public async Task ClearAsync()
    {
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

        await persistentState.WriteStateAsync();
    }

    private void EnsureIdentity()
    {
        if (!string.IsNullOrWhiteSpace(persistentState.State.UserId))
            return;

        ApplyIdentity(TryGetPrimaryKeyString());
    }

    private void ApplyIdentity(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        var parts = key.Split('/', 2);
        persistentState.State.WorkspaceId = parts.Length > 1 ? parts[0] : key;
        persistentState.State.UserId = parts.Length > 1 ? parts[1] : key;
    }

    private string? TryGetPrimaryKeyString()
    {
        try
        {
            return this.GetPrimaryKeyString();
        }
        catch (NullReferenceException)
        {
            return null;
        }
    }
}
