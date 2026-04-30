using Weave.Shared.Ids;

namespace Weave.Agents.Pipeline;

internal sealed record SkillMemoryEnrichment(string? Prompt, IReadOnlyList<SkillId> SkillIds);
