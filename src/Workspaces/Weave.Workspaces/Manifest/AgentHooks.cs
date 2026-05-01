namespace Weave.Workspaces.Models;

public sealed record AgentHooks
{
    public List<string> OnActivated { get; init; } = [];
    public List<string> OnDeactivated { get; init; } = [];
    public List<string> OnError { get; init; } = [];
}
