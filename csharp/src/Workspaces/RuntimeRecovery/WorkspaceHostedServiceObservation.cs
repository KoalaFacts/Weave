namespace Weave.Workspaces.RuntimeRecovery;

public sealed record WorkspaceHostedServiceObservation
{
    public WorkspaceRuntimeReadinessCondition Condition { get; init; }
    public string? Reason { get; init; }
    public IReadOnlyList<McpInstallationRuntimeObservation> McpInstallations { get; init; } = [];
}
