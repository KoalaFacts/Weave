using Microsoft.Extensions.Logging;
using Weave.Agents.Events;
using Weave.Agents.Models;
using Weave.Security.Scanning;
using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Agents.Actors;

public sealed class EpisodicMemoryActor(
    IEventBus eventBus,
    ILeakScanner leakScanner,
    TimeProvider timeProvider,
    ILogger<EpisodicMemoryActor> logger,
    IActorState<EpisodicMemoryState> persistentState) : IEpisodicMemoryActor
{
    private const string RedactedMarker = "***REDACTED***";

    public async Task<Episode> StoreEpisodeAsync(Episode episode)
    {
        var sanitized = await RedactEpisodeAsync(episode);
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

    private async Task<Episode> RedactEpisodeAsync(Episode episode)
    {
        var scanContext = new ScanContext
        {
            WorkspaceId = persistentState.State.WorkspaceId,
            SourceComponent = $"episodic-memory:{episode.AgentName}",
            Direction = ScanDirection.Inbound
        };

        var title = await RedactAsync(episode.Title, scanContext);
        var narrative = await RedactAsync(episode.Narrative, scanContext);
        var feedback = await RedactAsync(episode.ReviewFeedback, scanContext);
        var decisions = new List<EpisodeDecision>(episode.Decisions.Count);
        foreach (var decision in episode.Decisions)
        {
            decisions.Add(new EpisodeDecision
            {
                Question = await RedactAsync(decision.Question, scanContext) ?? decision.Question,
                ChosenOption = await RedactAsync(decision.ChosenOption, scanContext) ?? decision.ChosenOption
            });
        }

        return episode with
        {
            Title = title ?? episode.Title,
            Narrative = narrative ?? episode.Narrative,
            ReviewFeedback = feedback,
            Decisions = decisions
        };
    }

    private async Task<string?> RedactAsync(string? content, ScanContext context)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        var result = await leakScanner.ScanStringAsync(content, context);
        return result.HasLeaks ? ApplyRedactions(content, result.Findings) : content;
    }

    private static string ApplyRedactions(string content, IReadOnlyList<LeakFinding> findings)
    {
        // Apply replacements right-to-left so earlier offsets remain valid.
        var ordered = findings
            .Where(f => f.Length > 0 && f.Offset >= 0 && f.Offset < content.Length)
            .OrderByDescending(f => f.Offset)
            .ToList();

        if (ordered.Count == 0)
            return content;

        var span = content.AsSpan();
        var builder = new System.Text.StringBuilder(content.Length);
        var cursor = content.Length;
        foreach (var finding in ordered)
        {
            var end = Math.Min(finding.Offset + finding.Length, cursor);
            if (end <= finding.Offset)
                continue;
            builder.Insert(0, span[end..cursor]);
            builder.Insert(0, RedactedMarker);
            cursor = finding.Offset;
        }
        builder.Insert(0, span[..cursor]);
        return builder.ToString();
    }
}
