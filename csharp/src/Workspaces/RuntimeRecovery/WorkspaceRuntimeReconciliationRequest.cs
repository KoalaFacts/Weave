namespace Weave.Workspaces.RuntimeRecovery;

public sealed record WorkspaceRuntimeReconciliationRequest
{
    public required string ExpectedResourceSetDigest { get; init; }
    public string? ExpectedHostedServiceDigest { get; init; }
}
