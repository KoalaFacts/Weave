namespace Weave.Workspaces.RuntimeRecovery;

public sealed record WorkspaceRuntimeReconciliationResult
{
    public WorkspaceRuntimeReconciliationOutcome Outcome { get; init; }
    public string? Reason { get; init; }
    public bool HostedServicesRestored { get; init; }
    public WorkspaceRuntimeSnapshot? Observation { get; init; }
}
