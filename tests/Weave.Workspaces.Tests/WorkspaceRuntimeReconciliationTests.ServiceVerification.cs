using Weave.Management;
using Weave.Security.Tokens;
using Weave.Shared.Ids;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Runtime;
using Weave.Workspaces.RuntimeRecovery;

namespace Weave.Workspaces.Tests;

public sealed partial class WorkspaceRuntimeReconciliationTests
{
    [Theory]
    [InlineData(WorkspaceRuntimeReadinessCondition.NotReady)]
    [InlineData(WorkspaceRuntimeReadinessCondition.Unknown)]
    [InlineData((WorkspaceRuntimeReadinessCondition)99)]
    public async Task ReconcileAsync_ServiceVerificationUnhealthy_DoesNotConfirm(WorkspaceRuntimeReadinessCondition condition)
    {
        var fx = WithServices();
        var request = await fx.RequestAsync();
        var observation = new WorkspaceHostedServiceObservation
        {
            Condition = condition,
            McpInstallations = [new() { InstallationId = "ws/server", ToolName = "echo", Condition = condition, Reason = "mcp-tool-not-connected" }]
        };
        fx.Services.ObserveAsync(Arg.Any<WorkspaceHostedServices>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(observation);
        var result = await fx.ReconcileAsync(request);
        result.Outcome.ShouldBe(WorkspaceRuntimeReconciliationOutcome.Blocked);
        result.Reason.ShouldBe("hosted-services-not-ready");
        result.Observation!.HostedServiceObservation.ShouldBe(observation);
        result.HostedServicesRestored.ShouldBeFalse();
        fx.State.RecoveryCondition.ShouldBe(WorkspaceRecoveryCondition.RequiresReconciliation);
        fx.Writes.ShouldBe(0);
        fx.Journal.Received().Complete(Arg.Any<string>(), ManagementOperationOutcome.Failed, Arg.Any<DateTimeOffset>());
    }

    [Fact]
    public async Task ReconcileAsync_ServiceVerificationReady_ReturnsVerifiedObservationBeforeCommit()
    {
        var fx = WithServices();
        var request = await fx.RequestAsync();
        var restored = false;
        var verified = false;
        fx.Services.RestoreAsync(Arg.Any<WorkspaceHostedServices>(), Arg.Any<string>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(_ => { restored = true; return Task.FromResult<string?>(null); });
        fx.Services.ObserveAsync(Arg.Any<WorkspaceHostedServices>(), request.ExpectedHostedServiceDigest!, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                restored.ShouldBeTrue();
                verified = true;
                return new WorkspaceHostedServiceObservation { Condition = WorkspaceRuntimeReadinessCondition.Ready };
            });
        fx.Write = _ => { verified.ShouldBeTrue(); return Task.CompletedTask; };
        var result = await fx.ReconcileAsync(request);
        result.Outcome.ShouldBe(WorkspaceRuntimeReconciliationOutcome.Confirmed);
        result.HostedServicesRestored.ShouldBeTrue();
        result.Observation!.HostedServiceObservation!.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.Ready);
        fx.Writes.ShouldBe(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReconcileAsync_PlanChangesDuringServiceVerification_DoesNotConfirm(bool servicePlan)
    {
        var fx = WithServices();
        var request = await fx.RequestAsync();
        fx.Services.ObserveAsync(Arg.Any<WorkspaceHostedServices>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (servicePlan)
                    fx.Services.DescribeAsync(Arg.Any<WorkspaceHostedServices>(), Arg.Any<CancellationToken>())
                        .Returns(new WorkspaceHostedServicePlan { BlockReason = null, Digest = new string('b', 64) });
                else
                    fx.State.NetworkId = NetworkId.From(new string('e', 64));
                return new WorkspaceHostedServiceObservation { Condition = WorkspaceRuntimeReadinessCondition.Ready };
            });
        (await fx.ReconcileAsync(request)).Outcome.ShouldBe(WorkspaceRuntimeReconciliationOutcome.PlanChanged);
        fx.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task ReconcileAsync_ResourceStopsDuringServiceVerification_DoesNotConfirm()
    {
        var fx = WithServices();
        var request = await fx.RequestAsync();
        fx.Services.ObserveAsync(Arg.Any<WorkspaceHostedServices>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                fx.Runtime.ObserveContainerAsync(Arg.Any<ContainerId>(), Arg.Any<CancellationToken>()).Returns(ContainerRuntimeCondition.Stopped);
                return new WorkspaceHostedServiceObservation { Condition = WorkspaceRuntimeReadinessCondition.Ready };
            });
        var result = await fx.ReconcileAsync(request);
        result.Outcome.ShouldBe(WorkspaceRuntimeReconciliationOutcome.Blocked);
        result.Reason.ShouldBe("runtime-resources-not-ready");
        result.Observation!.HostedServiceObservation!.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.Ready);
        fx.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task ReconcileAsync_AuthorityRevokedDuringServiceVerification_RetainsUnknownAdmission()
    {
        var fx = WithServices();
        var request = await fx.RequestAsync();
        fx.Services.ObserveAsync(Arg.Any<WorkspaceHostedServices>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                fx.Authorizer.AuthorizeAsync(Arg.Any<CapabilityToken>(), "tool:echo:connect", "ws", Arg.Any<string>())
                    .Returns(Task.FromException(new UnauthorizedAccessException()));
                return new WorkspaceHostedServiceObservation { Condition = WorkspaceRuntimeReadinessCondition.Ready };
            });
        await Should.ThrowAsync<UnauthorizedAccessException>(() => fx.ReconcileAsync(request));
        AssertUnknown(fx);
    }

    [Fact]
    public async Task ReconcileAsync_CancelledDuringServiceVerification_RetainsUnknownAdmission()
    {
        var fx = WithServices();
        var request = await fx.RequestAsync();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        fx.Services.ObserveAsync(Arg.Any<WorkspaceHostedServices>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                caller.Cancel();
                return new WorkspaceHostedServiceObservation { Condition = WorkspaceRuntimeReadinessCondition.Ready };
            });
        await Should.ThrowAsync<OperationCanceledException>(() => fx.ReconcileAsync(request, caller.Token));
        AssertUnknown(fx);
    }

    [Fact]
    public async Task ReconcileAsync_ServiceVerificationThrows_RetainsUnknownAdmission()
    {
        var fx = WithServices();
        var request = await fx.RequestAsync();
        fx.Services.ObserveAsync(Arg.Any<WorkspaceHostedServices>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<WorkspaceHostedServiceObservation>(new IOException("verification unavailable")));
        await Should.ThrowAsync<IOException>(() => fx.ReconcileAsync(request));
        AssertUnknown(fx);
    }

    private static void AssertUnknown(Fixture fx)
    {
        fx.Journal.DidNotReceive().Complete(Arg.Any<string>(), Arg.Any<ManagementOperationOutcome>(), Arg.Any<DateTimeOffset>());
        fx.State.RecoveryCondition.ShouldBe(WorkspaceRecoveryCondition.RequiresReconciliation);
        fx.Writes.ShouldBe(0);
    }
}
