using Microsoft.Extensions.Logging.Abstractions;
using Weave.Management;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Runtime;
using Weave.Workspaces.RuntimeRecovery;

namespace Weave.Workspaces.Tests;

public sealed class WorkspaceRuntimeRecoveryTests
{
    private static readonly ContainerId Id = ContainerId.From(new string('b', 64));

    [Fact]
    public async Task RecoverAsync_NetworkNotRecorded_DoesNotDispatchOrChangeState()
    {
        var runtime = Substitute.For<IWorkspaceRuntime>();
        runtime.RuntimeName.Returns("podman");
        var state = State();
        state.NetworkId = null;
        var result = await Actor(state, runtime).RecoverContainerAsync(Id, new CapabilityToken(),
            Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(ContainerRecoveryOutcome.Blocked);
        result.Network.Condition.ShouldBe(NetworkRuntimeCondition.NotRecorded);
        result.Dispatched.ShouldBeFalse();
        state.NetworkId.ShouldBeNull();
        state.RecoveryCondition.ShouldBe(WorkspaceRecoveryCondition.RequiresReconciliation);
        await runtime.DidNotReceive().RecoverContainerAsync(Arg.Any<ContainerId>(), Arg.Any<NetworkId>(), Arg.Any<Func<Task>>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(NetworkRuntimeCondition.Missing)]
    [InlineData(NetworkRuntimeCondition.Unavailable)]
    [InlineData(NetworkRuntimeCondition.Present)]
    public async Task ObserveRuntimeAsync_NetworkState_ReportsDependencyWithoutMutation(NetworkRuntimeCondition condition)
    {
        var runtime = Substitute.For<IWorkspaceRuntime>();
        runtime.RuntimeName.Returns("podman");
        runtime.ObserveNetworkAsync(Arg.Any<NetworkId>(), Arg.Any<CancellationToken>()).Returns(condition);
        runtime.ObserveContainerNetworkAsync(Id, Arg.Any<NetworkId>(), Arg.Any<CancellationToken>()).Returns(ContainerNetworkCondition.Detached);
        var state = State();

        var observed = await Actor(state, runtime).ObserveRuntimeAsync(new CapabilityToken(), TestContext.Current.CancellationToken);

        observed.Network.NetworkId.ShouldBe(state.NetworkId?.ToString());
        observed.Network.Condition.ShouldBe(condition);
        observed.Containers.Single().NetworkAttachment.ShouldBe(condition is NetworkRuntimeCondition.Present
            ? ContainerNetworkCondition.Detached : ContainerNetworkCondition.NotChecked);
        state.RecoveryCondition.ShouldBe(WorkspaceRecoveryCondition.RequiresReconciliation);
        state.NetworkId.ShouldBe(NetworkId.From(new string('d', 64)));
    }

    [Fact]
    public async Task RuntimeOperations_UnknownWorkspaceOwner_DenyWithoutAdmissionOrEngineAccess()
    {
        var runtime = Substitute.For<IWorkspaceRuntime>();
        var journal = Substitute.For<IManagementOperationJournal>();
        var recovery = new WorkspaceRuntimeRecovery(runtime, Substitute.For<ICapabilityAuthorizer>(), journal, TimeProvider.System);
        var state = new WorkspaceState();

        await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            recovery.ObserveAsync(state, new CapabilityToken(), TestContext.Current.CancellationToken));
        await Should.ThrowAsync<UnauthorizedAccessException>(() => recovery.RecoverAsync(state, Id, new CapabilityToken(),
            Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken));

        journal.ReceivedCalls().ShouldBeEmpty();
        runtime.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task RecoverAsync_AdmissionStoreFails_BlocksBeforeRuntime()
    {
        var runtime = Substitute.For<IWorkspaceRuntime>();
        var journal = Substitute.For<IManagementOperationJournal>();
        journal.TryAdmit(Arg.Any<ManagementOperationRecord>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new IOException("journal unavailable"));
        var recovery = new WorkspaceRuntimeRecovery(runtime, Substitute.For<ICapabilityAuthorizer>(), journal, TimeProvider.System);

        await Should.ThrowAsync<IOException>(() => recovery.RecoverAsync(State(), Id, new CapabilityToken(),
            Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken));

        await runtime.DidNotReceive().RecoverContainerAsync(Arg.Any<ContainerId>(), Arg.Any<NetworkId>(), Arg.Any<Func<Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecoverAsync_CompletionNotPersisted_DoesNotReportSuccess()
    {
        var runtime = Substitute.For<IWorkspaceRuntime>();
        runtime.RuntimeName.Returns("podman");
        runtime.RecoverContainerAsync(Id, Arg.Any<NetworkId>(), Arg.Any<Func<Task>>(), Arg.Any<CancellationToken>()).Returns(new ContainerRecoveryResult
        {
            ContainerId = Id.ToString(),
            Outcome = ContainerRecoveryOutcome.Started,
            Condition = ContainerRuntimeCondition.Running,
            Dispatched = true
        });
        var journal = Substitute.For<IManagementOperationJournal>();
        journal.TryAdmit(Arg.Any<ManagementOperationRecord>(), Arg.Any<CancellationToken>()).Returns(true);
        journal.Complete(Arg.Any<string>(), Arg.Any<ManagementOperationOutcome>(), Arg.Any<DateTimeOffset>()).Returns(false);
        var recovery = new WorkspaceRuntimeRecovery(runtime, Substitute.For<ICapabilityAuthorizer>(), journal, TimeProvider.System);

        var result = await recovery.RecoverAsync(State(), Id, new CapabilityToken(),
            Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(ContainerRecoveryOutcome.EvidenceUnconfirmed);
        result.Dispatched.ShouldBeTrue();
        result.Condition.ShouldBe(ContainerRuntimeCondition.Running);
    }

    private static WorkspaceActor Actor(WorkspaceState state, IWorkspaceRuntime runtime)
    {
        var storage = Substitute.For<IActorState<WorkspaceState>>();
        storage.State.Returns(state);
        var journal = Substitute.For<IManagementOperationJournal>();
        journal.TryAdmit(Arg.Any<ManagementOperationRecord>(), Arg.Any<CancellationToken>()).Returns(true);
        journal.Complete(Arg.Any<string>(), Arg.Any<ManagementOperationOutcome>(), Arg.Any<DateTimeOffset>()).Returns(true);
        return new WorkspaceActor(runtime, Substitute.For<ILifecycleManager>(), Substitute.For<IEventBus>(),
            TimeProvider.System, NullLogger<WorkspaceActor>.Instance, storage, new WorkspaceRuntimeRecovery(runtime, Substitute.For<ICapabilityAuthorizer>(), journal, TimeProvider.System));
    }

    private static WorkspaceState State(string runtimeName = "podman", WorkspaceStatus status = WorkspaceStatus.Running) => new()
    {
        WorkspaceId = WorkspaceId.From("runtime-observation"),
        Status = status,
        RuntimeName = runtimeName,
        RuntimeInstanceId = Guid.NewGuid(),
        NetworkId = NetworkId.From(new string('d', 64)),
        RecoveryCondition = WorkspaceRecoveryCondition.RequiresReconciliation,
        Containers = [new ContainerInfo { ContainerId = Id, Name = "owned", Status = ContainerStatus.Running }]
    };

    [Fact]
    public async Task ObserveRuntimeAsync_StoppedContainer_ReportsRealityWithoutChangingRegistration()
    {
        var runtime = Substitute.For<IWorkspaceRuntime>();
        runtime.RuntimeName.Returns("podman");
        runtime.InstanceId.Returns(Guid.NewGuid());
        runtime.ObserveContainerAsync(Id, Arg.Any<CancellationToken>()).Returns(ContainerRuntimeCondition.Stopped);
        var state = State();

        var snapshot = await Actor(state, runtime).ObserveRuntimeAsync(new CapabilityToken(), TestContext.Current.CancellationToken);

        snapshot.Containers.Single().Condition.ShouldBe(ContainerRuntimeCondition.Stopped);
        snapshot.Containers.Single().RegisteredStatus.ShouldBe("Running");
        snapshot.StartedOnCurrentHost.ShouldBeFalse();
        snapshot.RecoveryCondition.ShouldBe("RequiresReconciliation");
        state.Status.ShouldBe(WorkspaceStatus.Running);
        state.Containers.Single().Status.ShouldBe(ContainerStatus.Running);
    }

    [Theory]
    [InlineData("docker", WorkspaceStatus.Running)]
    [InlineData("podman", WorkspaceStatus.Stopping)]
    [InlineData("podman", WorkspaceStatus.Error)]
    [InlineData("podman", WorkspaceStatus.Stopped)]
    public async Task RecoverContainerAsync_RuntimeOrLifecycleMismatch_BlocksWithoutDispatch(string creatingRuntime, WorkspaceStatus status)
    {
        var runtime = Substitute.For<IWorkspaceRuntime>();
        runtime.RuntimeName.Returns("podman");

        var result = await Actor(State(creatingRuntime, status), runtime).RecoverContainerAsync(Id, new CapabilityToken(), Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(ContainerRecoveryOutcome.Blocked);
        result.Dispatched.ShouldBeFalse();
        await runtime.DidNotReceive().RecoverContainerAsync(Arg.Any<ContainerId>(), Arg.Any<NetworkId>(), Arg.Any<Func<Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecoverContainerAsync_UnownedOrDuplicateId_BlocksWithoutDispatch()
    {
        var runtime = Substitute.For<IWorkspaceRuntime>();
        runtime.RuntimeName.Returns("podman");
        var state = State();
        var actor = Actor(state, runtime);

        (await actor.RecoverContainerAsync(ContainerId.From(new string('c', 64)), new CapabilityToken(), Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken))
            .Outcome.ShouldBe(ContainerRecoveryOutcome.Blocked);
        state.Containers.Add(state.Containers[0]);
        (await actor.RecoverContainerAsync(Id, new CapabilityToken(), Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken)).Outcome.ShouldBe(ContainerRecoveryOutcome.Blocked);
        await runtime.DidNotReceive().RecoverContainerAsync(Arg.Any<ContainerId>(), Arg.Any<NetworkId>(), Arg.Any<Func<Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecoverContainerAsync_HostChanged_PreservesReconciliationRequirement()
    {
        var runtime = Substitute.For<IWorkspaceRuntime>();
        runtime.RuntimeName.Returns("podman");
        runtime.InstanceId.Returns(Guid.NewGuid());
        runtime.RecoverContainerAsync(Id, Arg.Any<NetworkId>(), Arg.Any<Func<Task>>(), Arg.Any<CancellationToken>()).Returns(new ContainerRecoveryResult
        {
            ContainerId = Id.ToString(),
            Condition = ContainerRuntimeCondition.Running,
            Outcome = ContainerRecoveryOutcome.Started,
            Dispatched = true
        });
        var state = State();
        var previousInstance = state.RuntimeInstanceId;

        (await Actor(state, runtime).RecoverContainerAsync(Id, new CapabilityToken(), Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken))
            .Outcome.ShouldBe(ContainerRecoveryOutcome.Started);

        state.RecoveryCondition.ShouldBe(WorkspaceRecoveryCondition.RequiresReconciliation);
        state.RuntimeInstanceId.ShouldBe(previousInstance);
    }
}
