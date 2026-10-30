using System.Text.Json;

namespace Weave.Actions.Channel;

/// <summary>
/// Channels are returned as opaque <see cref="JsonElement"/> for the same
/// wire-fidelity reason as <see cref="Weave.Actions.Skill.ListSkillsResult"/>.
/// </summary>
public sealed record ListChannelsResult(IReadOnlyList<JsonElement> Channels);
