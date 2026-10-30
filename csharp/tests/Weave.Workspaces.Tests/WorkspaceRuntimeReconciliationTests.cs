using Weave.Management;
using Weave.Security.Tokens;
using Weave.Shared.Ids;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Runtime;
using Weave.Workspaces.RuntimeRecovery;

namespace Weave.Workspaces.Tests;

public sealed partial class WorkspaceRuntimeReconciliationTests
{
    [Fact]
    public async Task ReconcileAsync_HealthyRetainedResources_ConfirmsWithoutStartingOrChangingIdentity()
    {
        var fx = new Fixture();
        var oldInstance = fx.State.RuntimeInstanceId;
        var before = await fx.ObserveAsync();
        before.Readiness.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.NotReady);
        var result = await fx.ReconcileAsync();
        result.Outcome.ShouldBe(WorkspaceRuntimeReconciliationOutcome.Confirmed);
        fx.Writes.ShouldBe(1);
        fx.State.RuntimeInstanceId.ShouldNotBe(oldInstance);
        fx.State.RecoveryCondition.ShouldBe(WorkspaceRecoveryCondition.RuntimeReconciledOnThisHost);
        var after = await fx.ObserveAsync();
        after.Readiness.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.Ready);
        after.ConfirmedOnCurrentHost.ShouldBeTrue();
        after.StartedOnCurrentHost.ShouldBeFalse();
        after.ResourceSetDigest.ShouldNotBe(before.ResourceSetDigest);
        after.Containers.Single().ContainerId.ShouldBe(before.Containers.Single().ContainerId);
        await fx.Runtime.DidNotReceive().ProvisionAsync(Arg.Any<WorkspaceId>(), Arg.Any<Weave.Workspaces.Manifest.WorkspaceManifest>(), Arg.Any<CancellationToken>());
        await fx.Runtime.DidNotReceive().RecoverContainerAsync(Arg.Any<ContainerId>(), Arg.Any<NetworkId>(), Arg.Any<Func<Task>>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(NetworkRuntimeCondition.Missing, ContainerRuntimeCondition.Running, ContainerNetworkCondition.Attached)]
    [InlineData(NetworkRuntimeCondition.Unavailable, ContainerRuntimeCondition.Running, ContainerNetworkCondition.Attached)]
    [InlineData(NetworkRuntimeCondition.Present, ContainerRuntimeCondition.Stopped, ContainerNetworkCondition.Attached)]
    [InlineData(NetworkRuntimeCondition.Present, ContainerRuntimeCondition.Unknown, ContainerNetworkCondition.Attached)]
    [InlineData(NetworkRuntimeCondition.Present, ContainerRuntimeCondition.Running, ContainerNetworkCondition.Detached)]
    [InlineData(NetworkRuntimeCondition.Present, ContainerRuntimeCondition.Running, ContainerNetworkCondition.Unknown)]
    public async Task ReconcileAsync_UnhealthyOrUnknownResources_RetainsReconciliation(NetworkRuntimeCondition network,
        ContainerRuntimeCondition container, ContainerNetworkCondition attachment)
    {
        var fx = new Fixture();
        fx.Runtime.ObserveNetworkAsync(Arg.Any<NetworkId>(), Arg.Any<CancellationToken>()).Returns(network);
        fx.Runtime.ObserveContainerAsync(Arg.Any<ContainerId>(), Arg.Any<CancellationToken>()).Returns(container);
        fx.Runtime.ObserveContainerNetworkAsync(Arg.Any<ContainerId>(), Arg.Any<NetworkId>(), Arg.Any<CancellationToken>()).Returns(attachment);
        var result = await fx.ReconcileAsync();
        result.Outcome.ShouldBe(WorkspaceRuntimeReconciliationOutcome.Blocked);
        result.Reason.ShouldBe("runtime-resources-not-ready");
        fx.State.RecoveryCondition.ShouldBe(WorkspaceRecoveryCondition.RequiresReconciliation);
        fx.Writes.ShouldBe(0);
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("tool")]
    [InlineData("plugin")]
    [InlineData("installation")]
    public async Task ReconcileAsync_HostedServicesPresent_BlocksBeforeProbes(string kind)
    {
        var fx = new Fixture();
        switch (kind)
        {
            case "agent":
                fx.State.ActiveAgents.Add("worker");
                break;
            case "tool":
                fx.State.ActiveTools.Add("echo");
                break;
            case "plugin":
                fx.State.ActivePlugins.Add("connector");
                break;
            case "installation":
                fx.State.McpToolInstallations.Add(new() { Id = "ws/echo", DesiredEnabled = true });
                break;
        }
        var request = await fx.RequestAsync();
        fx.Runtime.ClearReceivedCalls();
        var result = await fx.ReconcileAsync(request);
        result.Reason.ShouldBe("hosted-services-require-restoration");
        result.Outcome.ShouldBe(WorkspaceRuntimeReconciliationOutcome.Blocked);
        fx.Runtime.ReceivedCalls().ShouldBeEmpty();
        fx.Writes.ShouldBe(0);
        fx.State.RecoveryCondition.ShouldBe(WorkspaceRecoveryCondition.RequiresReconciliation);
    }

    [Fact]
    public async Task ReconcileAsync_ResourceSetChangesDuringProbe_DoesNotCommit()
    {
        var fx = new Fixture();
        var request = await fx.RequestAsync();
        fx.Runtime.ObserveContainerAsync(Arg.Any<ContainerId>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            fx.State.NetworkId = NetworkId.From(new string('e', 64));
            return ContainerRuntimeCondition.Running;
        });
        (await fx.ReconcileAsync(request)).Outcome.ShouldBe(WorkspaceRuntimeReconciliationOutcome.PlanChanged);
        fx.Writes.ShouldBe(0);
        fx.State.RecoveryCondition.ShouldBe(WorkspaceRecoveryCondition.RequiresReconciliation);
    }

    [Theory]
    [InlineData("stopped", "runtime-resources-not-ready")]
    [InlineData("runtime-mismatch", "runtime-resources-not-ready")]
    [InlineData("network-unrecorded", "runtime-resources-not-ready")]
    [InlineData("duplicate-container", "invalid-runtime-identity")]
    [InlineData("unknown-condition", "recovery-condition-unconfirmed")]
    [InlineData("unknown-runtime-instance", "invalid-runtime-identity")]
    public async Task ReconcileAsync_IneligibleStoredState_DoesNotConfirm(string kind, string reason)
    {
        var fx = new Fixture();
        switch (kind)
        {
            case "stopped":
                fx.State.Status = WorkspaceStatus.Stopped;
                break;
            case "runtime-mismatch":
                fx.State.RuntimeName = "docker";
                break;
            case "network-unrecorded":
                fx.State.NetworkId = null;
                break;
            case "duplicate-container":
                fx.State.Containers.Add(fx.State.Containers.Single());
                break;
            case "unknown-condition":
                fx.State.RecoveryCondition = (WorkspaceRecoveryCondition)99;
                break;
            case "unknown-runtime-instance":
                fx.Runtime.InstanceId.Returns(Guid.Empty);
                break;
        }
        var result = await fx.ReconcileAsync();
        result.Outcome.ShouldBe(WorkspaceRuntimeReconciliationOutcome.Blocked);
        result.Reason.ShouldBe(reason);
        fx.Writes.ShouldBe(0);
        fx.State.RecoveryCondition.ShouldNotBe(WorkspaceRecoveryCondition.RuntimeReconciledOnThisHost);
    }

    [Fact]
    public async Task ReconcileAsync_CallerCancelsDuringWrite_AwaitsServiceOwnedCommit()
    {
        var fx = new Fixture();
        var request = await fx.RequestAsync();
        using var caller = new CancellationTokenSource();
        fx.Write = token =>
        {
            caller.Cancel();
            token.IsCancellationRequested.ShouldBeFalse();
            return Task.CompletedTask;
        };
        (await fx.ReconcileAsync(request, caller.Token)).Outcome.ShouldBe(WorkspaceRuntimeReconciliationOutcome.Confirmed);
        fx.State.RecoveryCondition.ShouldBe(WorkspaceRecoveryCondition.RuntimeReconciledOnThisHost);
        caller.IsCancellationRequested.ShouldBeTrue();
        fx.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task ReconcileAsync_AuthorityRevokedDuringProbe_DeniesBeforeCommit()
    {
        var fx = new Fixture();
        var calls = 0;
        fx.Authorizer.AuthorizeAsync(Arg.Any<CapabilityToken>(), WorkspaceRuntimeRecovery.ReconcileGrant, "ws", Arg.Any<string>())
            .Returns(_ => ++calls == 1 ? Task.CompletedTask : Task.FromException(new UnauthorizedAccessException()));
        await Should.ThrowAsync<UnauthorizedAccessException>(() => fx.ReconcileAsync());
        fx.Writes.ShouldBe(0);
        fx.State.RecoveryCondition.ShouldBe(WorkspaceRecoveryCondition.RequiresReconciliation);
    }

    [Fact]
    public async Task ReconcileAsync_AdmissionFails_DoesNotObserveOrCommit()
    {
        var fx = new Fixture();
        var request = await fx.RequestAsync();
        fx.Runtime.ClearReceivedCalls();
        fx.Journal.TryAdmit(Arg.Any<ManagementOperationRecord>(), Arg.Any<CancellationToken>()).Returns(_ => throw new IOException());
        await Should.ThrowAsync<IOException>(() => fx.ReconcileAsync(request));
        fx.Runtime.ReceivedCalls().ShouldBeEmpty();
        fx.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task ReconcileAsync_StorageWriteUnconfirmed_RetainsUnknownAndConservativeLiveState()
    {
        var fx = new Fixture();
        fx.Write = _ => throw new IOException("storage response lost");
        var result = await fx.ReconcileAsync();
        result.Outcome.ShouldBe(WorkspaceRuntimeReconciliationOutcome.OutcomeUnknown);
        fx.State.RecoveryCondition.ShouldBe(WorkspaceRecoveryCondition.RequiresReconciliation);
        (await fx.ObserveAsync()).Readiness.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.NotReady);
        fx.Journal.DidNotReceive().Complete(Arg.Any<string>(), Arg.Any<ManagementOperationOutcome>(), Arg.Any<DateTimeOffset>());
    }

    [Fact]
    public async Task ReconcileAsync_CompletionEvidenceFails_DoesNotReturnSuccess()
    {
        var fx = new Fixture();
        fx.Journal.Complete(Arg.Any<string>(), Arg.Any<ManagementOperationOutcome>(), Arg.Any<DateTimeOffset>()).Returns(false);
        (await fx.ReconcileAsync()).Outcome.ShouldBe(WorkspaceRuntimeReconciliationOutcome.EvidenceUnconfirmed);
        fx.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task ReconcileAsync_CancelledDuringProbe_DoesNotWriteState()
    {
        var fx = new Fixture();
        var request = await fx.RequestAsync();
        using var cancelled = new CancellationTokenSource();
        fx.Runtime.ObserveNetworkAsync(Arg.Any<NetworkId>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            cancelled.Cancel();
            return NetworkRuntimeCondition.Present;
        });
        await Should.ThrowAsync<OperationCanceledException>(() => fx.ReconcileAsync(request, cancelled.Token));
        fx.Writes.ShouldBe(0);
        fx.State.RecoveryCondition.ShouldBe(WorkspaceRecoveryCondition.RequiresReconciliation);
    }

    private sealed class Fixture
    {
        public IWorkspaceRuntime Runtime { get; } = Substitute.For<IWorkspaceRuntime>();
        public ICapabilityAuthorizer Authorizer { get; } = Substitute.For<ICapabilityAuthorizer>();
        public IManagementOperationJournal Journal { get; } = Substitute.For<IManagementOperationJournal>();
        public IWorkspaceHostedServiceRecovery Services { get; } = Substitute.For<IWorkspaceHostedServiceRecovery>();
        public WorkspaceState State { get; } = new()
        {
            WorkspaceId = WorkspaceId.From("ws"),
            Status = WorkspaceStatus.Running,
            RuntimeName = "podman",
            RuntimeInstanceId = Guid.NewGuid(),
            RecoveryCondition = WorkspaceRecoveryCondition.RequiresReconciliation,
            NetworkId = NetworkId.From(new string('d', 64)),
            Containers = [new ContainerInfo { ContainerId = ContainerId.From(new string('b', 64)), Name = "peer" }]
        };
        public int Writes { get; private set; }
        public Func<CancellationToken, Task>? Write { get; set; }
        private readonly WorkspaceRuntimeRecovery _recovery;

        public Fixture()
        {
            Runtime.RuntimeName.Returns("podman");
            Runtime.InstanceId.Returns(Guid.NewGuid());
            Runtime.ObserveNetworkAsync(Arg.Any<NetworkId>(), Arg.Any<CancellationToken>()).Returns(NetworkRuntimeCondition.Present);
            Runtime.ObserveContainerAsync(Arg.Any<ContainerId>(), Arg.Any<CancellationToken>()).Returns(ContainerRuntimeCondition.Running);
            Runtime.ObserveContainerNetworkAsync(Arg.Any<ContainerId>(), Arg.Any<NetworkId>(), Arg.Any<CancellationToken>()).Returns(ContainerNetworkCondition.Attached);
            Journal.TryAdmit(Arg.Any<ManagementOperationRecord>(), Arg.Any<CancellationToken>()).Returns(true);
            Journal.Complete(Arg.Any<string>(), Arg.Any<ManagementOperationOutcome>(), Arg.Any<DateTimeOffset>()).Returns(true);
            Services.DescribeAsync(Arg.Any<WorkspaceHostedServices>(), Arg.Any<CancellationToken>()).Returns(new WorkspaceHostedServicePlan());
            _recovery = new(Runtime, Authorizer, Journal, TimeProvider.System, Services);
        }
        public Task<WorkspaceRuntimeSnapshot> ObserveAsync() => _recovery.ObserveAsync(State, new(), TestContext.Current.CancellationToken);
        public async Task<WorkspaceRuntimeReconciliationRequest> RequestAsync()
        {
            var observed = await ObserveAsync();
            return new() { ExpectedResourceSetDigest = observed.ResourceSetDigest, ExpectedHostedServiceDigest = observed.HostedServicePlan?.Digest };
        }
        public async Task<WorkspaceRuntimeReconciliationResult> ReconcileAsync(WorkspaceRuntimeReconciliationRequest? request = null,
            CancellationToken? ct = null) => await _recovery.ReconcileAsync(State, request ?? await RequestAsync(), new(),
                Guid.NewGuid().ToString("N"), token => { Writes++; return Write?.Invoke(token) ?? Task.CompletedTask; }, ct ?? TestContext.Current.CancellationToken);
    }
}
