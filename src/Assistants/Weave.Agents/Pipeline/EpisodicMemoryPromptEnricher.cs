using Microsoft.Extensions.Logging;
using Weave.Agents.Lifecycle;
using Weave.Agents.Channels;
using Weave.Agents.Memory;
using Weave.Agents.Skills;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Models;
using Weave.Shared.Ids;

namespace Weave.Agents.Pipeline;

internal sealed class EpisodicMemoryPromptEnricher(
    IVirtualActorProvider actors,
    ILogger logger)
{
    public async Task<EpisodicMemoryEnrichment> EnrichAsync(
        WorkspaceId workspaceId,
        string agentName,
        string messageContent,
        string? prompt)
    {
        try
        {
            var episodicActor = actors.GetActor<IEpisodicMemoryActor>(VirtualActorId.From(workspaceId.ToString()));
            // Recall is workspace-wide so agents can learn from each other's past episodes,
            // matching skill-memory behavior. Callers wanting per-agent scope pass AgentName themselves.
            var results = await episodicActor.RecallAsync(
                messageContent,
                3,
                new EpisodeSearchOptions { PreferRecent = true });
            if (results.Count == 0)
                return new EpisodicMemoryEnrichment(prompt, []);

            var section = string.Join("\n\n", results.Select(r =>
            {
                var decisions = r.Episode.Decisions.Count == 0
                    ? string.Empty
                    : "\nDecisions: " + string.Join("; ", r.Episode.Decisions.Select(d =>
                        $"{d.Question} -> {d.ChosenOption}"));
                var review = string.IsNullOrWhiteSpace(r.Episode.ReviewFeedback)
                    ? string.Empty
                    : $"\nReview: {r.Episode.ReviewFeedback}";
                return $"### {r.Episode.Title} ({r.Episode.OccurredAt:yyyy-MM-dd}, relevance: {r.RelevanceScore:F1})\n{r.Episode.Narrative}{decisions}{review}";
            }));

            var block = $"[Relevant past episodes]\n{section}";
            var enrichedPrompt = string.IsNullOrWhiteSpace(prompt)
                ? block
                : $"{prompt}\n\n{block}";
            var episodeIds = results.Select(r => r.Episode.EpisodeId).Distinct().ToList();

            return new EpisodicMemoryEnrichment(enrichedPrompt, episodeIds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            logger.LogWarning(ex, "Failed to retrieve episodes for agent {AgentName}", agentName);
            return new EpisodicMemoryEnrichment(prompt, []);
        }
    }

    public async Task RecordRecallAsync(
        WorkspaceId workspaceId,
        string agentName,
        IReadOnlyList<EpisodeId> episodeIds)
    {
        if (episodeIds.Count == 0)
            return;

        try
        {
            var episodicActor = actors.GetActor<IEpisodicMemoryActor>(VirtualActorId.From(workspaceId.ToString()));
            foreach (var episodeId in episodeIds)
                await episodicActor.RecordRecallAsync(episodeId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            logger.LogWarning(ex, "Failed to record episode recall for agent {AgentName}", agentName);
        }
    }
}
