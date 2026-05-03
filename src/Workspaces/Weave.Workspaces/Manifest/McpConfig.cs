namespace Weave.Workspaces.Models;

public sealed record McpConfig
{
    public required string Server { get; init; }
    public IReadOnlyList<string> Args { get; init; } = [];
    public Dictionary<string, string> Env { get; init; } = [];
}
