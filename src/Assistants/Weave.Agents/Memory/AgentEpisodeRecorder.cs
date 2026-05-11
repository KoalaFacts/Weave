using Microsoft.Extensions.Logging;
using Weave.Agents.Lifecycle;
using Weave.Shared.Ids;

namespace Weave.Agents.Memory;

internal sealed class AgentEpisodeRecorder(
    IVirtualActorProvider actors,
    TimeProvider timeProvider,
    ILogger logger)
{
    public async Task<bool> StoreTaskEpisodeAsync(AgentState state, AgentTaskId taskId)
    {
        var task = state.ActiveTasks.FirstOrDefault(t => t.TaskId == taskId);
        if (task is null)
            return false;

        var episode = EpisodeExtractor.FromTask(task, state, timeProvider.GetUtcNow());
        if (episode is null)
            return false;

        try
        {
            var episodicActor = actors.GetActor<IEpisodicMemoryActor>(VirtualActorId.From(state.WorkspaceId.ToString()));
            await episodicActor.StoreEpisodeAsync(episode);
            state.LastEpisodeHistoryIndex = state.History.Count;
            logger.LogInformation(
                "Stored episode '{Title}' from task {TaskId}",
                episode.Title,
                taskId);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            logger.LogWarning(ex, "Failed to store episode from task {TaskId}", taskId);
            return false;
        }
    }

    public async Task StoreSessionEpisodeAsync(AgentState state)
    {
        var episode = EpisodeExtractor.FromSession(state, timeProvider.GetUtcNow());
        if (episode is null)
            return;

        try
        {
            var episodicActor = actors.GetActor<IEpisodicMemoryActor>(VirtualActorId.From(state.WorkspaceId.ToString()));
            await episodicActor.StoreEpisodeAsync(episode);
            state.LastEpisodeHistoryIndex = state.History.Count;
            logger.LogInformation(
                "Stored session episode '{Title}' for agent {AgentName}",
                episode.Title,
                state.AgentName);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            logger.LogWarning(ex, "Failed to store session episode for agent {AgentName}", state.AgentName);
        }
    }
}
