namespace Weave.Workspaces.RuntimeRecovery;

public sealed record McpInstallationRuntimeObservation
{
    public required string InstallationId { get; init; }
    public required string ToolName { get; init; }
    public WorkspaceRuntimeReadinessCondition Condition { get; init; }
    public string? Reason { get; init; }
}
