using Microsoft.Extensions.Logging.Abstractions;
using Weave.Invocations.Processes;
using Weave.Management;
using Weave.Security.Tokens;
using Weave.Shared.Ids;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Runtime;
using Weave.Workspaces.RuntimeRecovery;

namespace Weave.Workspaces.Tests;

[Trait("Category", "Integration")]
public sealed class WorkspaceRuntimeReconciliationIntegrationTests
{
    [Fact]
    public async Task ReconcileAsync_RealPodmanRetainedResources_ConfirmsWithoutProvisionOrReplay()
    {
        var runner = new ProcessCommandRunner(new ProcessRunner(TimeProvider.System, NullLogger<ProcessRunner>.Instance));
        var runtime = new ContainerRuntime(runner, new ContainerRuntimeOptions { Engine = "podman" },
            NullLogger<ContainerRuntime>.Instance);
        var journal = Substitute.For<IManagementOperationJournal>();
        journal.TryAdmit(Arg.Any<ManagementOperationRecord>(), Arg.Any<CancellationToken>()).Returns(true);
        journal.Complete(Arg.Any<string>(), Arg.Any<ManagementOperationOutcome>(), Arg.Any<DateTimeOffset>()).Returns(true);
        var recovery = new WorkspaceRuntimeRecovery(runtime, Substitute.For<ICapabilityAuthorizer>(), journal, TimeProvider.System, Substitute.For<IWorkspaceHostedServiceRecovery>());
        NetworkHandle? network = null;
        ContainerHandle? container = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            network = await runtime.CreateNetworkAsync(new() { Name = $"weave-reconcile-net-{Guid.NewGuid():N}"[..36] }, deadline.Token);
            container = await runtime.StartContainerAsync(new()
            {
                Name = $"weave-reconcile-{Guid.NewGuid():N}"[..32],
                Image = "docker.io/library/alpine:latest",
                Command = ["sleep", "120"],
                NetworkId = network.NetworkId
            }, deadline.Token);
            var state = new WorkspaceState
            {
                WorkspaceId = WorkspaceId.From("reconcile-integration"),
                Status = WorkspaceStatus.Running,
                RuntimeName = runtime.RuntimeName,
                RuntimeInstanceId = Guid.NewGuid(),
                RecoveryCondition = WorkspaceRecoveryCondition.RequiresReconciliation,
                NetworkId = network.NetworkId,
                Containers = [new() { ContainerId = container.ContainerId, Name = container.Name }]
            };
            var before = await recovery.ObserveAsync(state, new(), deadline.Token);
            before.Readiness.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.NotReady);
            var writes = 0;
            var result = await recovery.ReconcileAsync(state, new() { ExpectedResourceSetDigest = before.ResourceSetDigest },
                new(), Guid.NewGuid().ToString("N"), _ => { writes++; return Task.CompletedTask; }, deadline.Token);
            result.Outcome.ShouldBe(WorkspaceRuntimeReconciliationOutcome.Confirmed);
            writes.ShouldBe(1);
            var after = await recovery.ObserveAsync(state, new(), deadline.Token);
            after.Readiness.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.Ready);
            after.Containers.Single().ContainerId.ShouldBe(container.ContainerId.ToString());
            after.Network.NetworkId.ShouldBe(network.NetworkId.ToString());
            state.RuntimeInstanceId.ShouldBe(runtime.InstanceId);
        }
        finally
        {
            if (container is not null)
                await runtime.StopContainerAsync(container.ContainerId, CancellationToken.None);
            if (network is not null)
                await runtime.DeleteNetworkAsync(network.NetworkId, CancellationToken.None);
        }
    }
}
