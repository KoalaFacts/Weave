namespace Weave.Workspaces.Manifest;

public sealed record McpConfig
{
    public string? Server { get; init; }
    public IReadOnlyList<string> Args { get; init; } = [];
    public Dictionary<string, string> Env { get; init; } = [];
    public string? Url { get; init; }
}
