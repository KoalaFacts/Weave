using Microsoft.Extensions.Logging;
using Weave.Agents.Channels;
using Weave.Agents.Lifecycle;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Security.Scanning;
using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Memory;

public sealed class EpisodicMemoryActor(
    IEventBus eventBus,
    ILeakScanner leakScanner,
    TimeProvider timeProvider,
    ILogger<EpisodicMemoryActor> logger,
    IActorState<EpisodicMemoryState> persistentState) : IEpisodicMemoryActor
{
    private readonly EpisodeRedactor _redactor = new(leakScanner);

    public async Task<Episode> StoreEpisodeAsync(Episode episode)
    {
        var sanitized = await _redactor.RedactAsync(persistentState.State.WorkspaceId, episode);
        var key = sanitized.EpisodeId.ToString();
        persistentState.State.Episodes[key] = sanitized;
        await persistentState.WriteStateAsync();

        await eventBus.PublishAsync(new EpisodeStoredEvent
        {
            SourceId = key,
            WorkspaceId = WorkspaceId.From(persistentState.State.WorkspaceId),
            EpisodeId = sanitized.EpisodeId,
            Title = sanitized.Title,
            AgentName = sanitized.AgentName
        }, CancellationToken.None);

        logger.LogInformation(
            "Episode {EpisodeId} ({Title}) stored in workspace {WorkspaceId}",
            sanitized.EpisodeId,
            sanitized.Title,
            persistentState.State.WorkspaceId);

        return sanitized;
    }

    public Task<IReadOnlyList<EpisodeSearchResult>> RecallAsync(
        string query,
        int maxResults = 3,
        EpisodeSearchOptions? options = null) =>
        Task.FromResult(persistentState.State.Recall(query, maxResults, options, timeProvider.GetUtcNow()));

    public Task<Episode?> GetEpisodeAsync(EpisodeId episodeId)
    {
        persistentState.State.Episodes.TryGetValue(episodeId.ToString(), out var episode);
        return Task.FromResult(episode);
    }

    public Task<IReadOnlyList<Episode>> GetAllEpisodesAsync()
    {
        IReadOnlyList<Episode> episodes = persistentState.State.Episodes.Values
            .Where(e => e.ArchivedAt is null)
            .ToList();
        return Task.FromResult(episodes);
    }

    public async Task RecordRecallAsync(EpisodeId episodeId)
    {
        var key = episodeId.ToString();
        if (!persistentState.State.Episodes.TryGetValue(key, out var episode))
            return;

        episode.RecallCount++;
        episode.LastRecalledAt = timeProvider.GetUtcNow();
        await persistentState.WriteStateAsync();
    }

    public async Task<Episode?> ArchiveEpisodeAsync(EpisodeId episodeId)
    {
        var key = episodeId.ToString();
        if (!persistentState.State.Episodes.TryGetValue(key, out var episode))
            return null;

        episode.ArchivedAt ??= timeProvider.GetUtcNow();
        await persistentState.WriteStateAsync();
        logger.LogInformation(
            "Episode {EpisodeId} archived in workspace {WorkspaceId}",
            episodeId,
            persistentState.State.WorkspaceId);
        return episode;
    }

    public async Task RemoveEpisodeAsync(EpisodeId episodeId)
    {
        var key = episodeId.ToString();
        if (persistentState.State.Episodes.Remove(key))
        {
            await persistentState.WriteStateAsync();
            logger.LogInformation(
                "Episode {EpisodeId} removed from workspace {WorkspaceId}",
                episodeId,
                persistentState.State.WorkspaceId);
        }
    }

}
