using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Shared.Events;
using Weave.Shared.Ids;

namespace Weave.Silo.VirtualActors;

public sealed class EpisodicMemoryActorGrain : Grain, IEpisodicMemoryActorGrain
{
    private readonly EpisodicMemoryActor _actor;

    public EpisodicMemoryActorGrain(
        IEventBus eventBus,
        TimeProvider timeProvider,
        ILogger<EpisodicMemoryActor> logger,
        [PersistentState("episodic-memory", "Default")] IPersistentState<EpisodicMemoryState> state)
    {
        _actor = new EpisodicMemoryActor(eventBus, timeProvider, logger,
            new OrleansActorState<EpisodicMemoryState>(state));
    }

    public Task<Episode> StoreEpisodeAsync(Episode episode) => _actor.StoreEpisodeAsync(episode);
    public Task<IReadOnlyList<EpisodeSearchResult>> RecallAsync(string query, int maxResults = 3, EpisodeSearchOptions? options = null) =>
        _actor.RecallAsync(query, maxResults, options);
    public Task<Episode?> GetEpisodeAsync(EpisodeId episodeId) => _actor.GetEpisodeAsync(episodeId);
    public Task<IReadOnlyList<Episode>> GetAllEpisodesAsync() => _actor.GetAllEpisodesAsync();
    public Task RecordRecallAsync(EpisodeId episodeId) => _actor.RecordRecallAsync(episodeId);
    public Task<Episode?> ArchiveEpisodeAsync(EpisodeId episodeId) => _actor.ArchiveEpisodeAsync(episodeId);
    public Task RemoveEpisodeAsync(EpisodeId episodeId) => _actor.RemoveEpisodeAsync(episodeId);
}
