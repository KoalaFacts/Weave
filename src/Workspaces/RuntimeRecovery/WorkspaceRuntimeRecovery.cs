using System.Security.Cryptography;
using System.Text;
using Weave.Management;
using Weave.Security.Tokens;
using Weave.Shared.Ids;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Runtime;

namespace Weave.Workspaces.RuntimeRecovery;

public sealed class WorkspaceRuntimeRecovery(
    IWorkspaceRuntime runtime,
    ICapabilityAuthorizer authorizer,
    IManagementOperationJournal journal,
    TimeProvider timeProvider) : IWorkspaceRuntimeRecovery
{
    public const string ReadGrant = "workspace:runtime:read";
    public const string RecoverGrant = "workspace:runtime:recover";

    public async Task<WorkspaceRuntimeSnapshot> ObserveAsync(WorkspaceState state, CapabilityToken token, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (state.WorkspaceId.IsEmpty)
            throw new UnauthorizedAccessException("Workspace identity is unavailable.");
        await authorizer.AuthorizeAsync(token, ReadGrant, state.WorkspaceId.ToString());
        var network = new NetworkRuntimeObservation
        {
            NetworkId = state.NetworkId?.ToString(),
            Condition = state.NetworkId is null ? NetworkRuntimeCondition.NotRecorded
                : state.RuntimeName != runtime.RuntimeName ? NetworkRuntimeCondition.RuntimeMismatch
                : await runtime.ObserveNetworkAsync(state.NetworkId.Value, ct)
        };
        var observations = new List<WorkspaceContainerObservation>(state.Containers.Count);
        foreach (var container in state.Containers)
        {
            ct.ThrowIfCancellationRequested();
            var condition = state.RuntimeName == runtime.RuntimeName
                ? await runtime.ObserveContainerAsync(container.ContainerId, ct)
                : ContainerRuntimeCondition.RuntimeMismatch;
            observations.Add(new WorkspaceContainerObservation
            {
                ContainerId = container.ContainerId.ToString(),
                Name = container.Name,
                RegisteredStatus = container.Status.ToString(),
                Condition = condition,
                NetworkAttachment = network.Condition is NetworkRuntimeCondition.Present
                    ? await runtime.ObserveContainerNetworkAsync(container.ContainerId, state.NetworkId!.Value, ct)
                    : ContainerNetworkCondition.NotChecked
            });
        }
        return new WorkspaceRuntimeSnapshot
        {
            WorkspaceId = state.WorkspaceId.ToString(),
            RegisteredStatus = state.Status.ToString(),
            RecoveryCondition = state.RecoveryCondition.ToString(),
            CreatingRuntime = state.RuntimeName,
            CurrentRuntime = runtime.RuntimeName,
            StartedOnCurrentHost = state.RuntimeInstanceId != Guid.Empty && state.RuntimeInstanceId == runtime.InstanceId
                && state.RuntimeName == runtime.RuntimeName && state.Status is WorkspaceStatus.Running,
            ObservedAt = timeProvider.GetUtcNow(),
            Network = network,
            Containers = observations
        };
    }

    public async Task<ContainerRecoveryResult> RecoverAsync(WorkspaceState state, ContainerId containerId,
        CapabilityToken token, string managementId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (state.WorkspaceId.IsEmpty)
            throw new UnauthorizedAccessException("Workspace identity is unavailable.");
        var workspaceId = state.WorkspaceId.ToString();
        await authorizer.AuthorizeAsync(token, RecoverGrant, workspaceId);
        var result = new ContainerRecoveryResult { ContainerId = containerId.ToString() };
        if (!Guid.TryParseExact(managementId, "N", out var id) || id == Guid.Empty)
            return result with { Condition = ContainerRuntimeCondition.InvalidIdentity };
        managementId = id.ToString("N");
        var target = $"{workspaceId}/{containerId}";
        if (!journal.TryAdmit(new ManagementOperationRecord
        {
            Id = managementId,
            WorkspaceId = workspaceId,
            Subject = token.IssuedTo,
            TokenId = token.TokenId,
            Action = RecoverGrant,
            Target = target,
            AuthorizedGrants = RecoverGrant,
            RequestDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{target}|{state.NetworkId}"))),
            AdmittedAt = timeProvider.GetUtcNow()
        }, ct))
            return result with { Outcome = ContainerRecoveryOutcome.AlreadyAdmitted };

        try
        {
            if (state.Status is not WorkspaceStatus.Running)
                result = result with { Condition = ContainerRuntimeCondition.WorkspaceNotRunning };
            else if (state.Containers.Count(item => item.ContainerId == containerId) != 1)
                result = result with { Condition = ContainerRuntimeCondition.InvalidIdentity };
            else if (state.RuntimeName != runtime.RuntimeName)
                result = result with { Condition = ContainerRuntimeCondition.RuntimeMismatch };
            else if (state.NetworkId is null)
                result = result with { Network = new NetworkRuntimeObservation { Condition = NetworkRuntimeCondition.NotRecorded } };
            else
                result = await runtime.RecoverContainerAsync(containerId, state.NetworkId.Value,
                    () => authorizer.AuthorizeAsync(token, RecoverGrant, workspaceId), ct);
        }
        catch (UnauthorizedAccessException)
        {
            if (!journal.Complete(managementId, ManagementOperationOutcome.Failed, timeProvider.GetUtcNow()))
                return result with { Outcome = ContainerRecoveryOutcome.EvidenceUnconfirmed };
            throw;
        }
        if (result.Outcome is ContainerRecoveryOutcome.OutcomeUnknown)
            return result;
        var outcome = result.Outcome is ContainerRecoveryOutcome.Started or ContainerRecoveryOutcome.AlreadyRunning
            ? ManagementOperationOutcome.Succeeded : ManagementOperationOutcome.Failed;
        return journal.Complete(managementId, outcome, timeProvider.GetUtcNow())
            ? result : result with { Outcome = ContainerRecoveryOutcome.EvidenceUnconfirmed };
    }
}
