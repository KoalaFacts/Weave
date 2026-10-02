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
public sealed class WorkspaceRuntimeReadinessIntegrationTests
{
    [Fact]
    public async Task ObserveAsync_RealPodmanResources_TracksExternalChangesAndRetainsReconciliation()
    {
        var runner = new ProcessCommandRunner(new ProcessRunner(TimeProvider.System, NullLogger<ProcessRunner>.Instance));
        var runtime = new ContainerRuntime(runner, new ContainerRuntimeOptions { Engine = "podman" },
            NullLogger<ContainerRuntime>.Instance);
        var observation = new WorkspaceRuntimeRecovery(runtime, Substitute.For<ICapabilityAuthorizer>(),
            Substitute.For<IManagementOperationJournal>(), TimeProvider.System, Substitute.For<IWorkspaceHostedServiceRecovery>());
        ContainerHandle? container = null;
        NetworkHandle? network = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            network = await runtime.CreateNetworkAsync(new NetworkSpec
            { Name = $"weave-readiness-net-{Guid.NewGuid():N}"[..36] }, timeout.Token);
            container = await runtime.StartContainerAsync(new ContainerSpec
            {
                Name = $"weave-readiness-{Guid.NewGuid():N}"[..32],
                Image = "docker.io/library/alpine:latest",
                Command = ["sleep", "120"],
                NetworkId = network.NetworkId
            }, timeout.Token);
            var state = new WorkspaceState
            {
                WorkspaceId = WorkspaceId.From("readiness-integration"),
                Status = WorkspaceStatus.Running,
                RuntimeName = runtime.RuntimeName,
                RuntimeInstanceId = runtime.InstanceId,
                RecoveryCondition = WorkspaceRecoveryCondition.StartedOnThisHost,
                NetworkId = network.NetworkId,
                Containers = [new ContainerInfo { ContainerId = container.ContainerId, Name = container.Name, Status = ContainerStatus.Running }]
            };
            (await ObserveAsync()).Readiness.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.Ready);

            await runner.RunAsync("podman", ["stop", "--time", "1", container.ContainerId.ToString()], timeout.Token);
            var stopped = await ObserveAsync();
            stopped.Readiness.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.NotReady);
            stopped.Readiness.Reasons.ShouldContain(WorkspaceRuntimeReadinessReason.ContainerNotRunning);
            state.Containers.Single().Status.ShouldBe(ContainerStatus.Running);

            await runner.RunAsync("podman", ["start", container.ContainerId.ToString()], timeout.Token);
            (await ObserveAsync()).Readiness.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.Ready);

            state.RecoveryCondition = WorkspaceRecoveryCondition.RequiresReconciliation;
            var unreconciled = await ObserveAsync();
            unreconciled.Readiness.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.NotReady);
            unreconciled.Readiness.Reasons.ShouldContain(WorkspaceRuntimeReadinessReason.RequiresReconciliation);
            state.RecoveryCondition.ShouldBe(WorkspaceRecoveryCondition.RequiresReconciliation);

            await runner.RunAsync("podman", ["network", "disconnect", network.NetworkId.ToString(), container.ContainerId.ToString()], timeout.Token);
            var detached = await ObserveAsync();
            detached.Readiness.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.NotReady);
            detached.Readiness.Reasons.ShouldContain(WorkspaceRuntimeReadinessReason.ContainerNetworkNotAttached);

            Task<WorkspaceRuntimeSnapshot> ObserveAsync() =>
                observation.ObserveAsync(state, new CapabilityToken(), timeout.Token);
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
