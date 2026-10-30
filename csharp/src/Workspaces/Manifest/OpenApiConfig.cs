namespace Weave.Workspaces.Manifest;

public sealed record OpenApiConfig
{
    public required string SpecUrl { get; init; }
    public AuthConfig? Auth { get; init; }
}
