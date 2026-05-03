namespace Weave.Workspaces.Models;

public sealed record ToolHooks
{
    public IReadOnlyList<string> OnConnected { get; init; } = [];
    public IReadOnlyList<string> OnDisconnected { get; init; } = [];
    public IReadOnlyList<string> OnError { get; init; } = [];
}
