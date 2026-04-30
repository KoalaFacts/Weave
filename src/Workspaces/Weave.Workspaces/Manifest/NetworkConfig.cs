namespace Weave.Workspaces.Models;

public sealed record NetworkConfig
{
    public string? Name { get; init; }
    public string? Subnet { get; init; }
}