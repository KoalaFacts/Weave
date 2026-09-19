using System.Text.Json;

namespace Weave.Actions.Skill;

/// <summary>
/// Skills are returned as opaque <see cref="JsonElement"/> rather than a
/// curated summary because the only consumers today are workspace
/// export/import roundtrips, which need wire fidelity. A future user-facing
/// "list skills" command can add a typed <c>SkillSummary</c> action without
/// affecting this one.
/// </summary>
public sealed record ListSkillsResult(IReadOnlyList<JsonElement> Skills);
