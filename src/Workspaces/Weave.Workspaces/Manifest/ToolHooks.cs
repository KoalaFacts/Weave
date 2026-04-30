namespace Weave.Workspaces.Models;

public sealed record ToolHooks
{
    public List<string> OnConnected { get; init; } = [];
    public List<string> OnDisconnected { get; init; } = [];
    public List<string> OnError { get; init; } = [];
}