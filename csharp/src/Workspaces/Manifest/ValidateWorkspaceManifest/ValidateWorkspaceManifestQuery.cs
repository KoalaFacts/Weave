using Weave.Shared.Cqrs;

namespace Weave.Workspaces.Manifest;

/// <summary>
/// Validates a workspace manifest's JSON(C) text. Parses with the standard
/// <see cref="IManifestParser"/> (JSONC: comments + trailing commas
/// supported), runs structural validation, and returns the discovered
/// per-error list along with a small structural summary.
/// </summary>
public sealed record ValidateWorkspaceManifestQuery(string ManifestJson);

public sealed class ValidateWorkspaceManifestHandler(IManifestParser parser)
    : IQueryHandler<ValidateWorkspaceManifestQuery, ValidateWorkspaceManifestResult>
{
    public Task<ValidateWorkspaceManifestResult> HandleAsync(
        ValidateWorkspaceManifestQuery query,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var manifest = parser.Parse(query.ManifestJson);
        var errors = parser.Validate(manifest);
        return Task.FromResult(new ValidateWorkspaceManifestResult(
            Name: manifest.Name,
            AgentCount: manifest.Agents?.Count ?? 0,
            ToolCount: manifest.Tools?.Count ?? 0,
            TargetCount: manifest.Targets?.Count ?? 0,
            Errors: errors));
    }
}
