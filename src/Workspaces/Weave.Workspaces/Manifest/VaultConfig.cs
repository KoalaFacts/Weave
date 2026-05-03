namespace Weave.Workspaces.Models;

public sealed record VaultConfig
{
    public string? Address { get; init; }
    public string? Mount { get; init; }
}
