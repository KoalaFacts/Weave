namespace Weave.Workspaces.Manifest;

public sealed record VaultConfig
{
    public string? Address { get; init; }
    public string? Mount { get; init; }
}
