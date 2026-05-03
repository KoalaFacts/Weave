namespace Weave.Workspaces.Models;

public sealed record MemoryConfig
{
    public string Provider { get; init; } = "in-memory";
    public string? Ttl { get; init; }
}
