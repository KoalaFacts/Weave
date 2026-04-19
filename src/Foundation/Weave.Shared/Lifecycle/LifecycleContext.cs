using Weave.Shared.Ids;

namespace Weave.Shared.Lifecycle;

/// <summary>
/// Workspace lifecycle state passed through grain calls. Orleans
/// serialization is applied externally via a surrogate in
/// <c>Weave.Shared.Orleans</c>.
/// </summary>
public sealed record LifecycleContext
{
    public required WorkspaceId WorkspaceId { get; init; }
    public string? AgentName { get; init; }
    public string? ToolName { get; init; }
    public LifecyclePhase Phase { get; init; }
    public Dictionary<string, string> Properties { get; init; } = [];
}
