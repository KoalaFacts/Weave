using Weave.Shared.Events;

namespace Weave.Workspaces.Events;

public sealed record WorkspaceStartedEvent : DomainEvent
{
    public required string WorkspaceName { get; init; }
    public List<string> AgentNames { get; init; } = [];
}
public sealed record WorkspaceStoppedEvent : DomainEvent;
public sealed record WorkspaceErrorEvent : DomainEvent
{
    public required string ErrorMessage { get; init; }
}
