namespace Weave.Actions.Config;

/// <summary>
/// Result of <see cref="SetConfigAction"/>: echoes back the canonical-case
/// key and the persisted value so the frontend can render confirmation
/// without re-querying.
/// </summary>
public sealed record SetConfigResult(string Key, string Value);
