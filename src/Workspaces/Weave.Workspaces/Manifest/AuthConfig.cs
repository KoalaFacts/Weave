namespace Weave.Workspaces.Models;

public sealed record AuthConfig
{
    public required string Type { get; init; }
    public string? Token { get; init; }
}
