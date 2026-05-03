using Weave.Shared.Events;

namespace Weave.Workspaces.Events;

public sealed record WorkspaceStartedEvent : DomainEvent
{
    public required string WorkspaceName { get; init; }
    public IReadOnlyList<string> AgentNames { get; init; } = [];
}
