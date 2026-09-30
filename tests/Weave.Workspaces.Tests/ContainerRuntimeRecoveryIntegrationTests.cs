using Microsoft.Extensions.Logging.Abstractions;
using Weave.Management;
using Weave.Security.Tokens;
using Weave.Shared.Ids;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Runtime;
using Weave.Workspaces.RuntimeRecovery;

namespace Weave.Workspaces.Tests;

[Trait("Category", "Integration")]
public sealed class ContainerRuntimeRecoveryIntegrationTests
{
    [Fact]
    public async Task RecoverContainerAsync_RealStoppedContainer_StartsSameIdAndBlocksAfterRemoval()
    {
        var runner = new ProcessCommandRunner();
        var runtime = new ContainerRuntime(runner, new ContainerRuntimeOptions { Engine = "podman" },
            NullLogger<ContainerRuntime>.Instance);
        ContainerHandle? handle = null;
        NetworkHandle? network = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            network = await runtime.CreateNetworkAsync(new NetworkSpec { Name = $"weave-network-{Guid.NewGuid():N}"[..30] }, timeout.Token);
            handle = await runtime.StartContainerAsync(new ContainerSpec
            {
                Name = $"weave-observe-{Guid.NewGuid():N}"[..30],
                Image = "docker.io/library/alpine:latest",
                Command = ["sleep", "120"],
                NetworkId = network.NetworkId
            }, timeout.Token);
            (await runtime.ObserveContainerAsync(handle.ContainerId, timeout.Token))
                .ShouldBe(ContainerRuntimeCondition.Running);
            var state = new WorkspaceState
            {
                WorkspaceId = WorkspaceId.From("readiness-integration"),
                Status = WorkspaceStatus.Running,
                RuntimeName = runtime.RuntimeName,
                RuntimeInstanceId = runtime.InstanceId,
                RecoveryCondition = WorkspaceRecoveryCondition.StartedOnThisHost,
                NetworkId = network.NetworkId,
                Containers = [new ContainerInfo { ContainerId = handle.ContainerId, Name = handle.Name, Status = ContainerStatus.Running }]
            };
            var observation = new WorkspaceRuntimeRecovery(runtime, Substitute.For<ICapabilityAuthorizer>(),
                Substitute.For<IManagementOperationJournal>(), TimeProvider.System);
            (await observation.ObserveAsync(state, new CapabilityToken(), timeout.Token)).Readiness.Condition
                .ShouldBe(WorkspaceRuntimeReadinessCondition.Ready);
            await runner.RunAsync("podman", ["stop", "--time", "1", handle.ContainerId.ToString()], timeout.Token);
            (await runtime.ObserveContainerAsync(handle.ContainerId, timeout.Token))
                .ShouldBe(ContainerRuntimeCondition.Stopped);
            (await observation.ObserveAsync(state, new CapabilityToken(), timeout.Token)).Readiness.Condition
                .ShouldBe(WorkspaceRuntimeReadinessCondition.NotReady);

            var recovered = await runtime.RecoverContainerAsync(handle.ContainerId, network!.NetworkId, () => Task.CompletedTask, timeout.Token);

            recovered.ContainerId.ShouldBe(handle.ContainerId.ToString());
            recovered.Outcome.ShouldBe(ContainerRecoveryOutcome.Started);
            recovered.Condition.ShouldBe(ContainerRuntimeCondition.Running);
            recovered.Network.Condition.ShouldBe(NetworkRuntimeCondition.Present);
            recovered.NetworkAttachment.ShouldBe(ContainerNetworkCondition.Attached);
            (await observation.ObserveAsync(state, new CapabilityToken(), timeout.Token)).Readiness.Condition
                .ShouldBe(WorkspaceRuntimeReadinessCondition.Ready);
            state.RecoveryCondition = WorkspaceRecoveryCondition.RequiresReconciliation;
            var unreconciled = await observation.ObserveAsync(state, new CapabilityToken(), timeout.Token);
            unreconciled.Readiness.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.NotReady);
            unreconciled.Readiness.Reasons.ShouldContain(WorkspaceRuntimeReadinessReason.RequiresReconciliation);
            state.RecoveryCondition.ShouldBe(WorkspaceRecoveryCondition.RequiresReconciliation);
            (await runtime.RecoverContainerAsync(handle.ContainerId, network!.NetworkId, () => Task.CompletedTask, timeout.Token))
                .Outcome.ShouldBe(ContainerRecoveryOutcome.AlreadyRunning);
            await runner.RunAsync("podman", ["network", "disconnect", network!.NetworkId.ToString(), handle.ContainerId.ToString()], timeout.Token);
            var disconnected = await runtime.RecoverContainerAsync(handle.ContainerId, network.NetworkId, () => Task.CompletedTask, timeout.Token);
            disconnected.Outcome.ShouldBe(ContainerRecoveryOutcome.Blocked);
            disconnected.NetworkAttachment.ShouldBe(ContainerNetworkCondition.Detached);
            disconnected.Dispatched.ShouldBeFalse();
            var disconnectedSnapshot = await observation.ObserveAsync(state, new CapabilityToken(), timeout.Token);
            disconnectedSnapshot.Readiness.Reasons.ShouldContain(WorkspaceRuntimeReadinessReason.ContainerNetworkNotAttached);
            await runner.RunAsync("podman", ["stop", "--time", "1", handle.ContainerId.ToString()], timeout.Token);
            var detached = await runtime.RecoverContainerAsync(handle.ContainerId, network.NetworkId, () => Task.CompletedTask, timeout.Token);
            detached.Outcome.ShouldBe(ContainerRecoveryOutcome.Blocked);
            detached.NetworkAttachment.ShouldBeOneOf(ContainerNetworkCondition.Detached, ContainerNetworkCondition.Unknown);
            detached.Dispatched.ShouldBeFalse();
            (await runtime.ObserveContainerAsync(handle.ContainerId, timeout.Token)).ShouldBe(ContainerRuntimeCondition.Stopped);
            await runtime.DeleteNetworkAsync(network.NetworkId, timeout.Token);
            var networkMissing = await runtime.RecoverContainerAsync(handle.ContainerId, network.NetworkId, () => Task.CompletedTask, timeout.Token);
            networkMissing.Outcome.ShouldBe(ContainerRecoveryOutcome.Blocked);
            networkMissing.Network.Condition.ShouldBe(NetworkRuntimeCondition.Missing);
            networkMissing.Dispatched.ShouldBeFalse();
            await runtime.StopContainerAsync(handle.ContainerId, timeout.Token);
            var missing = await runtime.RecoverContainerAsync(handle.ContainerId, network!.NetworkId, () => Task.CompletedTask, timeout.Token);
            missing.Outcome.ShouldBe(ContainerRecoveryOutcome.Blocked);
            missing.Condition.ShouldBe(ContainerRuntimeCondition.Missing);
            missing.Dispatched.ShouldBeFalse();
        }
        finally
        {
            if (handle is not null)
                await runtime.StopContainerAsync(handle.ContainerId, CancellationToken.None);
            if (network is not null)
                await runtime.DeleteNetworkAsync(network.NetworkId, CancellationToken.None);
        }
    }
}
