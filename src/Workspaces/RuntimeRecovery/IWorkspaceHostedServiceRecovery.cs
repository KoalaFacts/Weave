using Weave.Security.Tokens;

namespace Weave.Workspaces.RuntimeRecovery;

public interface IWorkspaceHostedServiceRecovery
{
    Task<WorkspaceHostedServicePlan> DescribeAsync(WorkspaceHostedServices services, CancellationToken ct);
    Task<string?> RestoreAsync(WorkspaceHostedServices services, string expectedDigest, CapabilityToken token, CancellationToken ct);
}
