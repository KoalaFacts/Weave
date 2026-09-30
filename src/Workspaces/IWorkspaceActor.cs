using Weave.Security.Tokens;
using Weave.Shared.Ids;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.Runtime;
using Weave.Workspaces.RuntimeRecovery;
namespace Weave.Workspaces.Lifecycle;

public interface IWorkspaceActor
{
    Task<WorkspaceState> StartAsync(WorkspaceManifest manifest);
    Task StopAsync();
    Task<WorkspaceState> GetStateAsync();
    Task<WorkspaceRuntimeSnapshot> ObserveRuntimeAsync(CapabilityToken token, CancellationToken ct);
    Task<ContainerRecoveryResult> RecoverContainerAsync(ContainerId containerId, CapabilityToken token, string managementId, CancellationToken ct);
    Task SetDaprToolInstallationEnabledAsync(string pluginName, bool enabled);
    Task SetMcpToolInstallationEnabledAsync(string pluginName, bool enabled);
    Task PinMcpToolContractAsync(string pluginName, string contractDigest);
}
