namespace Weave.Workspaces.Manifest;

public sealed record AuthConfig
{
    public required string Type { get; init; }
    public string? Token { get; init; }
}
