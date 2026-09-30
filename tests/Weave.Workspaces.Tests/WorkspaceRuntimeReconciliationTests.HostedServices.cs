using Weave.Management;
using Weave.Security.Tokens;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Runtime;
using Weave.Workspaces.RuntimeRecovery;

namespace Weave.Workspaces.Tests;

public sealed partial class WorkspaceRuntimeReconciliationTests
{
    private static Fixture WithServices()
    {
        var fx = new Fixture();
        fx.State.ActiveTools.Add("echo");
        fx.State.ActivePlugins.Add("server");
        fx.State.McpToolInstallations.Add(new() { Id = "ws/server", PluginName = "server", DesiredEnabled = true });
        fx.Services.DescribeAsync(Arg.Any<WorkspaceHostedServices>(), Arg.Any<CancellationToken>()).Returns(new WorkspaceHostedServicePlan
        {
            BlockReason = null,
            Digest = new string('a', 64),
            RequiredGrants = ["plugin:invoke:ws/server", "tool:echo:connect"]
        });
        fx.Services.RestoreAsync(Arg.Any<WorkspaceHostedServices>(), Arg.Any<string>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        return fx;
    }

    [Fact]
    public async Task ReconcileAsync_SupportedServices_AdmitsExactGrantsBeforeRestoration()
    {
        var fx = WithServices();
        var admitted = false;
        fx.Journal.TryAdmit(Arg.Any<ManagementOperationRecord>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var record = call.Arg<ManagementOperationRecord>();
            record.AuthorizedGrants.ShouldBe("plugin:invoke:ws/server,tool:echo:connect,workspace:runtime:reconcile");
            admitted = true;
            return true;
        });
        fx.Services.RestoreAsync(Arg.Any<WorkspaceHostedServices>(), Arg.Any<string>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(_ => { admitted.ShouldBeTrue(); return Task.FromResult<string?>(null); });
        var result = await fx.ReconcileAsync();
        result.Outcome.ShouldBe(WorkspaceRuntimeReconciliationOutcome.Confirmed);
        result.HostedServicesRestored.ShouldBeTrue();
        fx.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task ReconcileAsync_ServiceAdmissionRejected_DoesNotRestore()
    {
        var fx = WithServices();
        fx.Journal.TryAdmit(Arg.Any<ManagementOperationRecord>(), Arg.Any<CancellationToken>()).Returns(false);
        (await fx.ReconcileAsync()).Outcome.ShouldBe(WorkspaceRuntimeReconciliationOutcome.AlreadyAdmitted);
        await fx.Services.DidNotReceive().RestoreAsync(Arg.Any<WorkspaceHostedServices>(), Arg.Any<string>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>());
        fx.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task ReconcileAsync_ServiceAdmissionUnavailable_DoesNotRestore()
    {
        var fx = WithServices();
        fx.Journal.TryAdmit(Arg.Any<ManagementOperationRecord>(), Arg.Any<CancellationToken>()).Returns(_ => throw new IOException("admission unavailable"));
        await Should.ThrowAsync<IOException>(() => fx.ReconcileAsync());
        await fx.Services.DidNotReceive().RestoreAsync(Arg.Any<WorkspaceHostedServices>(), Arg.Any<string>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>());
        fx.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task ReconcileAsync_ServiceCompletionEvidenceFails_DoesNotReportConfirmedRestoration()
    {
        var fx = WithServices();
        fx.Journal.Complete(Arg.Any<string>(), Arg.Any<ManagementOperationOutcome>(), Arg.Any<DateTimeOffset>()).Returns(false);
        var result = await fx.ReconcileAsync();
        result.Outcome.ShouldBe(WorkspaceRuntimeReconciliationOutcome.EvidenceUnconfirmed);
        result.HostedServicesRestored.ShouldBeFalse();
        fx.Writes.ShouldBe(1);
    }

    [Theory]
    [InlineData("tool:echo:connect")]
    [InlineData("plugin:invoke:ws/server")]
    public async Task ReconcileAsync_ServicePermissionDenied_DoesNotAdmitOrRestore(string grant)
    {
        var fx = WithServices();
        fx.Authorizer.AuthorizeAsync(Arg.Any<CapabilityToken>(), grant, "ws", Arg.Any<string>())
            .Returns(Task.FromException(new UnauthorizedAccessException()));
        await Should.ThrowAsync<UnauthorizedAccessException>(() => fx.ReconcileAsync());
        fx.Journal.DidNotReceive().TryAdmit(Arg.Any<ManagementOperationRecord>(), Arg.Any<CancellationToken>());
        await fx.Services.DidNotReceive().RestoreAsync(Arg.Any<WorkspaceHostedServices>(), Arg.Any<string>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>());
        fx.Writes.ShouldBe(0);
    }

    [Theory]
    [InlineData("mcp-peer-unavailable")]
    [InlineData("mcp-contract-rejected")]
    [InlineData("mcp-installation-not-restored")]
    public async Task ReconcileAsync_ServiceRestorationFails_PreservesBlockingCondition(string reason)
    {
        var fx = WithServices();
        fx.Services.RestoreAsync(Arg.Any<WorkspaceHostedServices>(), Arg.Any<string>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(reason));
        var result = await fx.ReconcileAsync();
        result.Outcome.ShouldBe(WorkspaceRuntimeReconciliationOutcome.Blocked);
        result.Reason.ShouldBe(reason);
        result.HostedServicesRestored.ShouldBeFalse();
        fx.State.RecoveryCondition.ShouldBe(WorkspaceRecoveryCondition.RequiresReconciliation);
        fx.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task ReconcileAsync_ServicePlanChanges_DoesNotRestore()
    {
        var fx = WithServices();
        var request = await fx.RequestAsync();
        fx.Services.DescribeAsync(Arg.Any<WorkspaceHostedServices>(), Arg.Any<CancellationToken>()).Returns(new WorkspaceHostedServicePlan
        {
            BlockReason = null,
            Digest = new string('b', 64),
            RequiredGrants = ["tool:echo:connect"]
        });
        (await fx.ReconcileAsync(request)).Outcome.ShouldBe(WorkspaceRuntimeReconciliationOutcome.PlanChanged);
        await fx.Services.DidNotReceive().RestoreAsync(Arg.Any<WorkspaceHostedServices>(), Arg.Any<string>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>());
        fx.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task ReconcileAsync_ResourcesStopDuringRestoration_DoesNotConfirm()
    {
        var fx = WithServices();
        fx.Services.RestoreAsync(Arg.Any<WorkspaceHostedServices>(), Arg.Any<string>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                fx.Runtime.ObserveContainerAsync(Arg.Any<Weave.Shared.Ids.ContainerId>(), Arg.Any<CancellationToken>()).Returns(ContainerRuntimeCondition.Stopped);
                return Task.FromResult<string?>(null);
            });
        var result = await fx.ReconcileAsync();
        result.Reason.ShouldBe("runtime-resources-not-ready");
        result.Outcome.ShouldBe(WorkspaceRuntimeReconciliationOutcome.Blocked);
        fx.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task ReconcileAsync_AuthorityRevokedAfterRestoration_RetainsUnknownAdmission()
    {
        var fx = WithServices();
        fx.Services.RestoreAsync(Arg.Any<WorkspaceHostedServices>(), Arg.Any<string>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                fx.Authorizer.AuthorizeAsync(Arg.Any<CapabilityToken>(), "tool:echo:connect", "ws", Arg.Any<string>())
                    .Returns(Task.FromException(new UnauthorizedAccessException()));
                return Task.FromResult<string?>(null);
            });
        await Should.ThrowAsync<UnauthorizedAccessException>(() => fx.ReconcileAsync());
        fx.Journal.DidNotReceive().Complete(Arg.Any<string>(), Arg.Any<ManagementOperationOutcome>(), Arg.Any<DateTimeOffset>());
        fx.State.RecoveryCondition.ShouldBe(WorkspaceRecoveryCondition.RequiresReconciliation);
        fx.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task ReconcileAsync_CallerCancelsRestoration_DoesNotCommitOrClaimNoEffect()
    {
        var fx = WithServices();
        var request = await fx.RequestAsync();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        fx.Services.RestoreAsync(Arg.Any<WorkspaceHostedServices>(), Arg.Any<string>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                caller.Cancel();
                return Task.FromCanceled<string?>(call.Arg<CancellationToken>());
            });
        await Should.ThrowAsync<OperationCanceledException>(() => fx.ReconcileAsync(request, caller.Token));
        fx.Journal.DidNotReceive().Complete(Arg.Any<string>(), Arg.Any<ManagementOperationOutcome>(), Arg.Any<DateTimeOffset>());
        fx.Writes.ShouldBe(0);
    }
}
