namespace Weave.Workspaces.Manifest;

public sealed record NetworkConfig
{
    public string? Name { get; init; }
    public string? Subnet { get; init; }
}
