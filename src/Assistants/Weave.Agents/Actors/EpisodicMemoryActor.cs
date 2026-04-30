using Microsoft.Extensions.Logging;
using Weave.Agents.Events;
using Weave.Agents.Models;
using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Actors;

public sealed class EpisodicMemoryActor(
    IEventBus eventBus,
    TimeProvider timeProvider,
    ILogger<EpisodicMemoryActor> logger,
    IActorState<EpisodicMemoryState> persistentState) : IEpisodicMemoryActor
{
    public async Task<Episode> StoreEpisodeAsync(Episode episode)
    {
        var key = episode.EpisodeId.ToString();
        persistentState.State.Episodes[key] = episode;
        await persistentState.WriteStateAsync();

        await eventBus.PublishAsync(new EpisodeStoredEvent
        {
            SourceId = key,
            WorkspaceId = WorkspaceId.From(persistentState.State.WorkspaceId),
            EpisodeId = episode.EpisodeId,
            Title = episode.Title,
            AgentName = episode.AgentName
        }, CancellationToken.None);

        logger.LogInformation(
            "Episode {EpisodeId} ({Title}) stored in workspace {WorkspaceId}",
            episode.EpisodeId,
            episode.Title,
            persistentState.State.WorkspaceId);

        return episode;
    }

    public Task<IReadOnlyList<EpisodeSearchResult>> RecallAsync(
        string query,
        int maxResults = 3,
        EpisodeSearchOptions? options = null)
    {
        if (persistentState.State.Episodes.Count == 0)
            return Task.FromResult<IReadOnlyList<EpisodeSearchResult>>(new List<EpisodeSearchResult>());

        var queryTokens = SkillSearchScorer.Tokenize(query);
        if (queryTokens.Length == 0)
            return Task.FromResult<IReadOnlyList<EpisodeSearchResult>>(new List<EpisodeSearchResult>());

        var effectiveOptions = options ?? new EpisodeSearchOptions();
        var now = timeProvider.GetUtcNow();
        var scored = new List<EpisodeSearchResult>();

        foreach (var episode in persistentState.State.Episodes.Values)
        {
            if (episode.ArchivedAt is not null)
                continue;
            if (effectiveOptions.AgentName is { } agentName && !string.Equals(episode.AgentName, agentName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (effectiveOptions.Tag is { } tag && !episode.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                continue;
            if (effectiveOptions.Since is { } since && episode.OccurredAt < since)
                continue;

            var score = EpisodeSearchScorer.ComputeRelevanceScore(episode, queryTokens, effectiveOptions, now);
            if (score > 0)
                scored.Add(new EpisodeSearchResult { Episode = episode, RelevanceScore = score });
        }

        IReadOnlyList<EpisodeSearchResult> results = scored
            .OrderByDescending(r => r.RelevanceScore)
            .Take(maxResults)
            .ToList();

        return Task.FromResult(results);
    }

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
