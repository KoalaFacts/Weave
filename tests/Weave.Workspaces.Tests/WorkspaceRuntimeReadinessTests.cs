using Microsoft.Extensions.Logging.Abstractions;
using Weave.Management;
using Weave.Security.Tokens;
using Weave.Shared.Ids;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.Runtime;
using Weave.Workspaces.RuntimeRecovery;

namespace Weave.Workspaces.Tests;

public sealed class WorkspaceRuntimeReadinessTests
{
    private static readonly ContainerId Id = ContainerId.From(new string('b', 64));
    private static readonly NetworkId Network = NetworkId.From(new string('d', 64));

    private static IWorkspaceRuntime Runtime()
    {
        var runtime = Substitute.For<IWorkspaceRuntime>();
        runtime.RuntimeName.Returns("podman");
        runtime.InstanceId.Returns(Guid.NewGuid());
        runtime.ObserveNetworkAsync(Network, Arg.Any<CancellationToken>()).Returns(NetworkRuntimeCondition.Present);
        runtime.ObserveContainerAsync(Id, Arg.Any<CancellationToken>()).Returns(ContainerRuntimeCondition.Running);
        runtime.ObserveContainerNetworkAsync(Id, Network, Arg.Any<CancellationToken>()).Returns(ContainerNetworkCondition.Attached);
        return runtime;
    }

    private static WorkspaceState State(IWorkspaceRuntime runtime) => new()
    {
        WorkspaceId = WorkspaceId.From("readiness"),
        Status = WorkspaceStatus.Running,
        RuntimeName = runtime.RuntimeName,
        RuntimeInstanceId = runtime.InstanceId,
        RecoveryCondition = WorkspaceRecoveryCondition.StartedOnThisHost,
        NetworkId = Network,
        Containers = [new ContainerInfo { ContainerId = Id, Name = "owned", Status = ContainerStatus.Running }]
    };

    private static WorkspaceRuntimeRecovery Recovery(IWorkspaceRuntime runtime) =>
        new(runtime, Substitute.For<ICapabilityAuthorizer>(), Substitute.For<IManagementOperationJournal>(), TimeProvider.System, Substitute.For<IWorkspaceHostedServiceRecovery>());

    [Fact]
    public async Task ObserveAsync_CurrentHostAndHealthyResources_ReportsReadyWithoutMutation()
    {
        var runtime = Runtime();
        var state = State(runtime);
        var journal = Substitute.For<IManagementOperationJournal>();
        var clock = Substitute.For<TimeProvider>();
        clock.GetUtcNow().Returns(new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero));
        var recovery = new WorkspaceRuntimeRecovery(runtime, Substitute.For<ICapabilityAuthorizer>(), journal, clock, Substitute.For<IWorkspaceHostedServiceRecovery>());

        var snapshot = await recovery.ObserveAsync(state, new CapabilityToken(), TestContext.Current.CancellationToken);

        snapshot.Readiness.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.Ready);
        snapshot.Readiness.Reasons.ShouldBeEmpty();
        snapshot.ObservedAt.ShouldBe(clock.GetUtcNow());
        snapshot.StartedOnCurrentHost.ShouldBeTrue();
        state.RecoveryCondition.ShouldBe(WorkspaceRecoveryCondition.StartedOnThisHost);
        state.Containers.Single().Status.ShouldBe(ContainerStatus.Running);
        journal.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(NetworkRuntimeCondition.Missing, WorkspaceRuntimeReadinessCondition.NotReady, WorkspaceRuntimeReadinessReason.NetworkNotReady)]
    [InlineData(NetworkRuntimeCondition.NotRecorded, WorkspaceRuntimeReadinessCondition.NotReady, WorkspaceRuntimeReadinessReason.NetworkNotReady)]
    [InlineData(NetworkRuntimeCondition.InvalidIdentity, WorkspaceRuntimeReadinessCondition.NotReady, WorkspaceRuntimeReadinessReason.NetworkNotReady)]
    [InlineData(NetworkRuntimeCondition.Unknown, WorkspaceRuntimeReadinessCondition.Unknown, WorkspaceRuntimeReadinessReason.NetworkObservationIncomplete)]
    [InlineData(NetworkRuntimeCondition.Unavailable, WorkspaceRuntimeReadinessCondition.Unknown, WorkspaceRuntimeReadinessReason.NetworkObservationIncomplete)]
    [InlineData(NetworkRuntimeCondition.Unsupported, WorkspaceRuntimeReadinessCondition.Unknown, WorkspaceRuntimeReadinessReason.NetworkObservationIncomplete)]
    public async Task ObserveAsync_NetworkNotConfirmed_DoesNotReportReady(NetworkRuntimeCondition network,
        WorkspaceRuntimeReadinessCondition expected, WorkspaceRuntimeReadinessReason reason)
    {
        var runtime = Runtime();
        runtime.ObserveNetworkAsync(Network, Arg.Any<CancellationToken>()).Returns(network);
        var state = State(runtime);
        if (network is NetworkRuntimeCondition.NotRecorded)
            state.NetworkId = null;

        var snapshot = await Recovery(runtime).ObserveAsync(state, new CapabilityToken(), TestContext.Current.CancellationToken);

        snapshot.Readiness.Condition.ShouldBe(expected);
        snapshot.Readiness.Reasons.ShouldContain(reason);
        snapshot.Network.Condition.ShouldBe(network);
    }

    [Theory]
    [InlineData(ContainerRuntimeCondition.Stopped, WorkspaceRuntimeReadinessCondition.NotReady)]
    [InlineData(ContainerRuntimeCondition.Missing, WorkspaceRuntimeReadinessCondition.NotReady)]
    [InlineData(ContainerRuntimeCondition.Transitioning, WorkspaceRuntimeReadinessCondition.NotReady)]
    [InlineData(ContainerRuntimeCondition.InvalidIdentity, WorkspaceRuntimeReadinessCondition.NotReady)]
    [InlineData(ContainerRuntimeCondition.Unknown, WorkspaceRuntimeReadinessCondition.Unknown)]
    [InlineData(ContainerRuntimeCondition.Unavailable, WorkspaceRuntimeReadinessCondition.Unknown)]
    [InlineData(ContainerRuntimeCondition.Unsupported, WorkspaceRuntimeReadinessCondition.Unknown)]
    public async Task ObserveAsync_ContainerNotConfirmed_UsesObservedState(ContainerRuntimeCondition container,
        WorkspaceRuntimeReadinessCondition expected)
    {
        var runtime = Runtime();
        runtime.ObserveContainerAsync(Id, Arg.Any<CancellationToken>()).Returns(container);
        var state = State(runtime);

        var snapshot = await Recovery(runtime).ObserveAsync(state, new CapabilityToken(), TestContext.Current.CancellationToken);

        snapshot.Readiness.Condition.ShouldBe(expected);
        snapshot.Readiness.Reasons.ShouldBe(new[] { expected is WorkspaceRuntimeReadinessCondition.NotReady
            ? WorkspaceRuntimeReadinessReason.ContainerNotRunning : WorkspaceRuntimeReadinessReason.ContainerObservationIncomplete });
        state.Containers.Single().Status.ShouldBe(ContainerStatus.Running);
    }

    [Theory]
    [InlineData(ContainerNetworkCondition.Detached, WorkspaceRuntimeReadinessCondition.NotReady)]
    [InlineData(ContainerNetworkCondition.InvalidIdentity, WorkspaceRuntimeReadinessCondition.NotReady)]
    [InlineData(ContainerNetworkCondition.Unknown, WorkspaceRuntimeReadinessCondition.Unknown)]
    [InlineData(ContainerNetworkCondition.Unavailable, WorkspaceRuntimeReadinessCondition.Unknown)]
    [InlineData(ContainerNetworkCondition.Unsupported, WorkspaceRuntimeReadinessCondition.Unknown)]
    [InlineData(ContainerNetworkCondition.NotChecked, WorkspaceRuntimeReadinessCondition.Unknown)]
    public async Task ObserveAsync_AttachmentNotConfirmed_DoesNotReportReady(ContainerNetworkCondition attachment,
        WorkspaceRuntimeReadinessCondition expected)
    {
        var runtime = Runtime();
        runtime.ObserveContainerNetworkAsync(Id, Network, Arg.Any<CancellationToken>()).Returns(attachment);

        var snapshot = await Recovery(runtime).ObserveAsync(State(runtime), new CapabilityToken(), TestContext.Current.CancellationToken);

        snapshot.Readiness.Condition.ShouldBe(expected);
        snapshot.Readiness.Reasons.ShouldBe(new[] { expected is WorkspaceRuntimeReadinessCondition.NotReady
            ? WorkspaceRuntimeReadinessReason.ContainerNetworkNotAttached : WorkspaceRuntimeReadinessReason.ContainerNetworkObservationIncomplete });
    }

    [Fact]
    public async Task ObserveAsync_ReconciliationRequiredAndUnknownProbe_KeepsKnownBlocker()
    {
        var runtime = Runtime();
        runtime.ObserveContainerAsync(Id, Arg.Any<CancellationToken>()).Returns(ContainerRuntimeCondition.Unavailable);
        var state = State(runtime);
        state.RecoveryCondition = WorkspaceRecoveryCondition.RequiresReconciliation;
        state.RuntimeInstanceId = Guid.NewGuid();

        var snapshot = await Recovery(runtime).ObserveAsync(state, new CapabilityToken(), TestContext.Current.CancellationToken);

        snapshot.Readiness.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.NotReady);
        snapshot.Readiness.Reasons.ShouldBe(new[] { WorkspaceRuntimeReadinessReason.RequiresReconciliation,
            WorkspaceRuntimeReadinessReason.NotStartedOnCurrentHost, WorkspaceRuntimeReadinessReason.ContainerObservationIncomplete });
        state.RecoveryCondition.ShouldBe(WorkspaceRecoveryCondition.RequiresReconciliation);
    }

    [Fact]
    public async Task ObserveAsync_InProcessWorkspace_ReportsReadyWithoutExternalNetwork()
    {
        var runtime = new InProcessRuntime(NullLogger<InProcessRuntime>.Instance);
        var environment = await runtime.ProvisionAsync(WorkspaceId.From("readiness"), new WorkspaceManifest
        { Name = "readiness", Version = "1.0" }, TestContext.Current.CancellationToken);
        var state = State(runtime) with { NetworkId = environment.NetworkId, Containers = [] };

        var snapshot = await Recovery(runtime).ObserveAsync(state, new CapabilityToken(), TestContext.Current.CancellationToken);

        snapshot.Readiness.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.Ready);
        snapshot.Network.Condition.ShouldBe(NetworkRuntimeCondition.NotRequired);
        snapshot.Containers.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(WorkspaceStatus.Starting)]
    [InlineData(WorkspaceStatus.Stopping)]
    [InlineData(WorkspaceStatus.Stopped)]
    [InlineData(WorkspaceStatus.Error)]
    public async Task ObserveAsync_WorkspaceNotRunning_ReportsNotReadyEvenWithHealthyResources(WorkspaceStatus status)
    {
        var runtime = Runtime();
        var state = State(runtime);
        state.Status = status;

        var snapshot = await Recovery(runtime).ObserveAsync(state, new CapabilityToken(), TestContext.Current.CancellationToken);

        snapshot.Readiness.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.NotReady);
        snapshot.Readiness.Reasons.ShouldContain(WorkspaceRuntimeReadinessReason.WorkspaceNotRunning);
        state.Status.ShouldBe(status);
    }

    [Theory]
    [InlineData(WorkspaceRecoveryCondition.RequiresReconciliation, WorkspaceRuntimeReadinessCondition.NotReady)]
    [InlineData(WorkspaceRecoveryCondition.NotApplicable, WorkspaceRuntimeReadinessCondition.Unknown)]
    public async Task ObserveAsync_RecoveryNotConfirmed_DoesNotReportReady(WorkspaceRecoveryCondition condition,
        WorkspaceRuntimeReadinessCondition expected)
    {
        var runtime = Runtime();
        var state = State(runtime);
        state.RecoveryCondition = condition;

        var snapshot = await Recovery(runtime).ObserveAsync(state, new CapabilityToken(), TestContext.Current.CancellationToken);

        snapshot.Readiness.Condition.ShouldBe(expected);
        snapshot.Readiness.Reasons.ShouldBe(new[] { condition is WorkspaceRecoveryCondition.RequiresReconciliation
            ? WorkspaceRuntimeReadinessReason.RequiresReconciliation : WorkspaceRuntimeReadinessReason.RecoveryConditionUnconfirmed });
        state.RecoveryCondition.ShouldBe(condition);
    }

    [Theory]
    [InlineData("podman")]
    [InlineData("docker")]
    public async Task ObserveAsync_RuntimeOwnerChanged_DoesNotReportReady(string creatingRuntime)
    {
        var runtime = Runtime();
        var state = State(runtime);
        state.RuntimeName = creatingRuntime;
        state.RuntimeInstanceId = Guid.NewGuid();

        var snapshot = await Recovery(runtime).ObserveAsync(state, new CapabilityToken(), TestContext.Current.CancellationToken);

        snapshot.Readiness.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.NotReady);
        snapshot.Readiness.Reasons.ShouldContain(WorkspaceRuntimeReadinessReason.NotStartedOnCurrentHost);
        if (creatingRuntime == "docker")
        {
            snapshot.Network.Condition.ShouldBe(NetworkRuntimeCondition.RuntimeMismatch);
            snapshot.Containers.Single().Condition.ShouldBe(ContainerRuntimeCondition.RuntimeMismatch);
        }
        state.RecoveryCondition.ShouldBe(WorkspaceRecoveryCondition.StartedOnThisHost);
    }

    [Fact]
    public async Task ObserveAsync_DeniedAuthority_DoesNotProbeResources()
    {
        var runtime = Runtime();
        var state = State(runtime);
        runtime.ClearReceivedCalls();
        var authorizer = Substitute.For<ICapabilityAuthorizer>();
        authorizer.AuthorizeAsync(Arg.Any<CapabilityToken>(), WorkspaceRuntimeRecovery.ReadGrant, "readiness", Arg.Any<string>())
            .Returns(_ => throw new UnauthorizedAccessException());
        var recovery = new WorkspaceRuntimeRecovery(runtime, authorizer, Substitute.For<IManagementOperationJournal>(), TimeProvider.System, Substitute.For<IWorkspaceHostedServiceRecovery>());

        await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            recovery.ObserveAsync(state, new CapabilityToken(), TestContext.Current.CancellationToken));

        runtime.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task ObserveAsync_CancelledDuringLastProbe_DoesNotReturnReady()
    {
        var runtime = Runtime();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        runtime.ObserveContainerNetworkAsync(Id, Network, cancellation.Token).Returns(_ =>
        {
            cancellation.Cancel();
            return ContainerNetworkCondition.Attached;
        });

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Recovery(runtime).ObserveAsync(State(runtime), new CapabilityToken(), cancellation.Token));
    }
}
