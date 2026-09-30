using Weave.Tools.InstallMcpTool;
using Weave.Workspaces.Lifecycle;

namespace Weave.Workspaces.RuntimeRecovery;

public sealed partial class WorkspaceRuntimeRecovery
{
    private static WorkspaceHostedServices CaptureServices(WorkspaceState state) => new()
    {
        WorkspaceId = state.WorkspaceId.ToString(),
        Tools = state.ActiveTools.ToArray(),
        Plugins = state.ActivePlugins.ToArray(),
        McpInstallations = state.McpToolInstallations.Where(item => item.DesiredEnabled)
            .Select(McpToolInstallationSnapshot.From).ToArray()
    };

    private async Task<WorkspaceHostedServicePlan?> DescribeServicesAsync(WorkspaceState state, CancellationToken ct)
    {
        if (state.ActiveAgents.Count > 0 || state.DaprToolInstallations.Any(item => item.DesiredEnabled))
            return new();
        if (state.ActiveTools.Count == 0 && state.ActivePlugins.Count == 0
            && !state.McpToolInstallations.Any(item => item.DesiredEnabled))
            return null;
        return await hostedServices.DescribeAsync(CaptureServices(state), ct);
    }
}
