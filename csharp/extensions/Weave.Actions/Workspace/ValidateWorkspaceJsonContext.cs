using System.Text.Json.Serialization;

namespace Weave.Actions.Workspace;

/// <summary>
/// Wire shape sent to <c>POST /api/workspaces/validate</c>: the raw JSON(C)
/// text of the manifest. Internal because frontends pass a path through
/// <see cref="ValidateWorkspaceInput"/>; the action reads the file before
/// serializing this body.
/// </summary>
internal sealed record ValidateWorkspaceWire
{
    public string ManifestJson { get; init; } = string.Empty;
}

/// <summary>
/// Wire shape returned by <c>POST /api/workspaces/validate</c>: the
/// manifest's structural summary plus the per-error list. Internal because
/// frontends consume the curated <see cref="ValidateWorkspaceResult"/>.
/// </summary>
internal sealed record ValidateWorkspaceResultWire
{
    public string Name { get; init; } = string.Empty;
    public int AgentCount { get; init; }
    public int ToolCount { get; init; }
    public int TargetCount { get; init; }
    public List<string> Errors { get; init; } = [];
}

/// <summary>
/// Minimal subset of RFC 7807 ProblemDetails the action needs to extract a
/// human-readable message when the silo returns 400 (parse failure). Only
/// the <c>errors</c> map is ever read; everything else is ignored.
/// </summary>
internal sealed record ProblemWire
{
    public Dictionary<string, string[]>? Errors { get; init; }
}

/// <summary>
/// Per-feature source-gen JSON context for the validate-workspace verb.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ValidateWorkspaceWire))]
[JsonSerializable(typeof(ValidateWorkspaceResultWire))]
[JsonSerializable(typeof(ProblemWire))]
internal sealed partial class ValidateWorkspaceJsonContext : JsonSerializerContext;
