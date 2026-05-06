using Weave.Workspaces.Manifest;

namespace Weave.Silo.Api;

/// <summary>
/// Response body for <c>POST /api/workspaces/validate</c>: the manifest's
/// structural summary plus the per-error list. Empty <see cref="Errors"/>
/// means the manifest is valid; non-empty means structurally invalid (a
/// 200 OK still carries the errors — only parse failures return 400).
/// </summary>
public sealed record ValidateWorkspaceManifestResponse
{
    public required string Name { get; init; }
    public required int AgentCount { get; init; }
    public required int ToolCount { get; init; }
    public required int TargetCount { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }

    public static ValidateWorkspaceManifestResponse FromResult(ValidateWorkspaceManifestResult result) => new()
    {
        Name = result.Name,
        AgentCount = result.AgentCount,
        ToolCount = result.ToolCount,
        TargetCount = result.TargetCount,
        Errors = result.Errors
    };
}
