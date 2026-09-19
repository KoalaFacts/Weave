namespace Weave.Silo.Api;

/// <summary>
/// Request body for <c>POST /api/workspaces/validate</c>: the raw JSON(C)
/// text of a workspace manifest to validate. The silo parses with the
/// standard JSONC parser (comments + trailing commas supported) and runs
/// structural validation.
/// </summary>
public sealed record ValidateWorkspaceManifestRequest
{
    public required string ManifestJson { get; init; }
}
