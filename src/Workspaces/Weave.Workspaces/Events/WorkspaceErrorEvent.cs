using Weave.Shared.Events;

namespace Weave.Workspaces.Events;

public sealed record WorkspaceErrorEvent : DomainEvent
{
    public required string ErrorMessage { get; init; }
}
