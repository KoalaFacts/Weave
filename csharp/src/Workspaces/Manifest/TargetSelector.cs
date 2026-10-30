namespace Weave.Workspaces.Manifest;

public sealed record TargetSelector
{
    public IReadOnlyList<string> Labels { get; init; } = [];
}
