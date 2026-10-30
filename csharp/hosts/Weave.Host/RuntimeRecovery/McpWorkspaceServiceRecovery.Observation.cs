using Weave.Shared.VirtualActors;
using Weave.Silo.Plugins;
using Weave.Tools.InstallMcpTool;
using Weave.Tools.Tool;
using Weave.Workspaces.RuntimeRecovery;

namespace Weave.Silo.RuntimeRecovery;

internal sealed partial class McpWorkspaceServiceRecovery
{
    public async Task<WorkspaceHostedServiceObservation> ObserveAsync(WorkspaceHostedServices services,
        string expectedDigest, CancellationToken ct)
    {
        var plan = await DescribeAsync(services, ct);
        if (plan.BlockReason is not null || plan.Digest != expectedDigest)
            return new() { Reason = "hosted-service-plan-changed" };
        var observations = new List<McpInstallationRuntimeObservation>(services.McpInstallations.Count);
        foreach (var installation in services.McpInstallations)
        {
            ct.ThrowIfCancellationRequested();
            string? reason = "mcp-installation-not-restored";
            var condition = WorkspaceRuntimeReadinessCondition.NotReady;
            if (installations.MatchesInstallation(installation))
            {
                var failure = await probe.ProbeAsync(new McpToolInstallation
                {
                    Url = installation.Url,
                    ServerName = installation.ServerName,
                    ServerVersion = installation.ServerVersion,
                    Operation = installation.Operation,
                    ContractDigest = installation.ContractDigest
                }, ct);
                (condition, reason) = failure switch
                {
                    InstallationFailureCode.None => (WorkspaceRuntimeReadinessCondition.Ready, null),
                    InstallationFailureCode.ContractRejected => (WorkspaceRuntimeReadinessCondition.NotReady, "mcp-contract-rejected"),
                    InstallationFailureCode.PeerUnavailable => (WorkspaceRuntimeReadinessCondition.NotReady, "mcp-peer-unavailable"),
                    _ => (WorkspaceRuntimeReadinessCondition.Unknown, "mcp-observation-incomplete")
                };
                if (condition is WorkspaceRuntimeReadinessCondition.Ready)
                {
                    if (!services.Tools.Contains(installation.Operation, StringComparer.Ordinal))
                        (condition, reason) = (WorkspaceRuntimeReadinessCondition.NotReady, "mcp-tool-not-recorded");
                    else if (!await actors.GetActor<IToolActor>(VirtualActorId.From($"{services.WorkspaceId}/{installation.Operation}"))
                        .HasCurrentConnectionAsync(ToolType.Mcp, installation.Id, ct))
                        (condition, reason) = (WorkspaceRuntimeReadinessCondition.NotReady, "mcp-tool-not-connected");
                }
                if (!installations.MatchesInstallation(installation))
                    (condition, reason) = (WorkspaceRuntimeReadinessCondition.NotReady, "mcp-installation-not-restored");
            }
            observations.Add(new()
            {
                InstallationId = installation.Id,
                ToolName = installation.Operation,
                Condition = condition,
                Reason = reason
            });
        }
        ct.ThrowIfCancellationRequested();
        if ((await DescribeAsync(services, ct)).Digest != expectedDigest)
            return new() { Reason = "hosted-service-plan-changed", McpInstallations = observations.AsReadOnly() };
        return new()
        {
            Condition = observations.Any(item => item.Condition is WorkspaceRuntimeReadinessCondition.NotReady)
                ? WorkspaceRuntimeReadinessCondition.NotReady
                : observations.Any(item => item.Condition is WorkspaceRuntimeReadinessCondition.Unknown)
                    ? WorkspaceRuntimeReadinessCondition.Unknown : WorkspaceRuntimeReadinessCondition.Ready,
            McpInstallations = observations.AsReadOnly()
        };
    }
}
