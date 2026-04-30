namespace Weave.Workspaces.Models;

public sealed record McpConfig
{
    public required string Server { get; init; }
    public List<string> Args { get; init; } = [];
    public Dictionary<string, string> Env { get; init; } = [];
}
