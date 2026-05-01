namespace Weave.Workspaces.Models;

public sealed record StorageConfig
{
    public required string Backend { get; init; }
    public string? ConnectionString { get; init; }
    public string? Schema { get; init; }
    public StorageIsolation Isolation { get; init; } = StorageIsolation.Database;
    public string? Database { get; init; }
}
