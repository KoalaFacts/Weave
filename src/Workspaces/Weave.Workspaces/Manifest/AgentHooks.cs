namespace Weave.Workspaces.Models;

public sealed record AgentHooks
{
    public IReadOnlyList<string> OnActivated { get; init; } = [];
    public IReadOnlyList<string> OnDeactivated { get; init; } = [];
    public IReadOnlyList<string> OnError { get; init; } = [];
}
