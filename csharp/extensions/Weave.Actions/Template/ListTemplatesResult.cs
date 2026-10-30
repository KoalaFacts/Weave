using System.Text.Json;

namespace Weave.Actions.Template;

/// <summary>
/// Templates returned as opaque <see cref="JsonElement"/> for the same
/// wire-fidelity reason as the workspace import/export skill and channel
/// lists.
/// </summary>
public sealed record ListTemplatesResult(IReadOnlyList<JsonElement> Templates);
