namespace Weave.Workspaces.Models;

public sealed record DirectHttpConfig
{
    public required string BaseUrl { get; init; }
    public AuthConfig? Auth { get; init; }
}
