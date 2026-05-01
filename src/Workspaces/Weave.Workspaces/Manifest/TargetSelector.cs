namespace Weave.Workspaces.Models;

public sealed record TargetSelector
{
    public List<string> Labels { get; init; } = [];
}
