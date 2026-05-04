using Weave.Shared.Events;

namespace Weave.Workspaces.Lifecycle;

public sealed record WorkspaceErrorEvent : DomainEvent
{
    public required string ErrorMessage { get; init; }
}
