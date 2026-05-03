namespace Weave.Workspaces.Models;

public sealed record TargetSelector
{
    public IReadOnlyList<string> Labels { get; init; } = [];
}
