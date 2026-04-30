using Weave.Shared.Events;

namespace Weave.Workspaces.Events;

public sealed record WorkspaceStartedEvent : DomainEvent
{
    public required string WorkspaceName { get; init; }
    public List<string> AgentNames { get; init; } = [];
}
