using Weave.Agents.Models;
using Weave.Shared.Ids;

namespace Weave.Agents.Memory;

public interface IEpisodicMemoryActor
{
    Task<Episode> StoreEpisodeAsync(Episode episode);
    Task<IReadOnlyList<EpisodeSearchResult>> RecallAsync(string query, int maxResults = 3, EpisodeSearchOptions? options = null);
    Task<Episode?> GetEpisodeAsync(EpisodeId episodeId);
    Task<IReadOnlyList<Episode>> GetAllEpisodesAsync();
    Task RecordRecallAsync(EpisodeId episodeId);
    Task<Episode?> ArchiveEpisodeAsync(EpisodeId episodeId);
    Task RemoveEpisodeAsync(EpisodeId episodeId);
}
