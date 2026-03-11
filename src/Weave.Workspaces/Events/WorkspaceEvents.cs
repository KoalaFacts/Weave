using Orleans;
using Weave.Shared.Events;

namespace Weave.Workspaces.Events;

[GenerateSerializer]
public sealed record WorkspaceStartedEvent : DomainEvent
{
    [Id(3)] public required string WorkspaceName { get; init; }
    [Id(4)] public List<string> AgentNames { get; init; } = [];
}

[GenerateSerializer]
public sealed record WorkspaceStoppedEvent : DomainEvent;

[GenerateSerializer]
public sealed record WorkspaceErrorEvent : DomainEvent
{
    [Id(3)] public required string ErrorMessage { get; init; }
}
