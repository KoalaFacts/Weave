using Weave.Shared.Ids;

namespace Weave.Agents.Pipeline;

internal sealed record EpisodicMemoryEnrichment(string? Prompt, IReadOnlyList<EpisodeId> EpisodeIds);
