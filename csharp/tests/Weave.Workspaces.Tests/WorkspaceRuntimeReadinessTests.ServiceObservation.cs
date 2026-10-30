using Weave.Management;
using Weave.Security.Tokens;
using Weave.Workspaces.Runtime;
using Weave.Workspaces.RuntimeRecovery;

namespace Weave.Workspaces.Tests;

public sealed partial class WorkspaceRuntimeReadinessTests
{
    [Fact]
    public async Task ObserveAsync_NoHostedServices_OmitsServiceObservation()
    {
        var runtime = Runtime();
        var services = Substitute.For<IWorkspaceHostedServiceRecovery>();
        var journal = Substitute.For<IManagementOperationJournal>();
        var recovery = new WorkspaceRuntimeRecovery(runtime, Substitute.For<ICapabilityAuthorizer>(), journal, TimeProvider.System, services);
        var result = await recovery.ObserveAsync(State(runtime), new(), TestContext.Current.CancellationToken);
        result.Readiness.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.Ready);
        result.HostedServiceObservation.ShouldBeNull();
        services.ReceivedCalls().ShouldBeEmpty();
        journal.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task ObserveAsync_UnsupportedHostedServices_DoesNotProbeThem()
    {
        var runtime = Runtime();
        var state = State(runtime);
        state.ActiveAgents.Add("agent");
        var services = Substitute.For<IWorkspaceHostedServiceRecovery>();
        var recovery = new WorkspaceRuntimeRecovery(runtime, Substitute.For<ICapabilityAuthorizer>(),
            Substitute.For<IManagementOperationJournal>(), TimeProvider.System, services);
        var result = await recovery.ObserveAsync(state, new(), TestContext.Current.CancellationToken);
        result.HostedServiceObservation!.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.Unknown);
        result.HostedServiceObservation.Reason.ShouldBe("hosted-services-require-restoration");
        result.Readiness.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.Unknown);
        result.Readiness.Reasons.Select(reason => reason.ToString()).ShouldBe(["HostedServiceObservationIncomplete"]);
        services.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(NetworkRuntimeCondition.Present, WorkspaceRuntimeReadinessCondition.Ready, WorkspaceRuntimeReadinessCondition.Ready)]
    [InlineData(NetworkRuntimeCondition.Present, WorkspaceRuntimeReadinessCondition.NotReady, WorkspaceRuntimeReadinessCondition.NotReady)]
    [InlineData(NetworkRuntimeCondition.Present, WorkspaceRuntimeReadinessCondition.Unknown, WorkspaceRuntimeReadinessCondition.Unknown)]
    [InlineData(NetworkRuntimeCondition.Present, (WorkspaceRuntimeReadinessCondition)99, WorkspaceRuntimeReadinessCondition.Unknown)]
    [InlineData(NetworkRuntimeCondition.Unavailable, WorkspaceRuntimeReadinessCondition.Ready, WorkspaceRuntimeReadinessCondition.Unknown)]
    [InlineData(NetworkRuntimeCondition.Unavailable, WorkspaceRuntimeReadinessCondition.NotReady, WorkspaceRuntimeReadinessCondition.NotReady)]
    [InlineData(NetworkRuntimeCondition.Unavailable, WorkspaceRuntimeReadinessCondition.Unknown, WorkspaceRuntimeReadinessCondition.Unknown)]
    [InlineData(NetworkRuntimeCondition.Missing, WorkspaceRuntimeReadinessCondition.Ready, WorkspaceRuntimeReadinessCondition.NotReady)]
    [InlineData(NetworkRuntimeCondition.Missing, WorkspaceRuntimeReadinessCondition.NotReady, WorkspaceRuntimeReadinessCondition.NotReady)]
    [InlineData(NetworkRuntimeCondition.Missing, WorkspaceRuntimeReadinessCondition.Unknown, WorkspaceRuntimeReadinessCondition.NotReady)]
    public async Task ObserveAsync_ResourceAndServiceConditions_CombinesReadinessWithoutEffects(
        NetworkRuntimeCondition networkCondition, WorkspaceRuntimeReadinessCondition serviceCondition,
        WorkspaceRuntimeReadinessCondition expected)
    {
        var runtime = Runtime();
        runtime.ObserveNetworkAsync(Network, Arg.Any<CancellationToken>()).Returns(networkCondition);
        var state = State(runtime);
        state.ActiveTools.Add("echo");
        var originalCondition = state.RecoveryCondition;
        var originalInstance = state.RuntimeInstanceId;
        var services = Substitute.For<IWorkspaceHostedServiceRecovery>();
        services.DescribeAsync(Arg.Any<WorkspaceHostedServices>(), Arg.Any<CancellationToken>())
            .Returns(new WorkspaceHostedServicePlan { BlockReason = null, Digest = new string('a', 64) });
        services.ObserveAsync(Arg.Any<WorkspaceHostedServices>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WorkspaceHostedServiceObservation { Condition = serviceCondition });
        var journal = Substitute.For<IManagementOperationJournal>();
        var recovery = new WorkspaceRuntimeRecovery(runtime, Substitute.For<ICapabilityAuthorizer>(), journal, TimeProvider.System, services);

        var result = await recovery.ObserveAsync(state, new(), TestContext.Current.CancellationToken);

        result.Readiness.Condition.ShouldBe(expected);
        result.HostedServiceObservation!.Condition.ShouldBe(serviceCondition);
        if (serviceCondition is WorkspaceRuntimeReadinessCondition.Ready)
        {
            result.Readiness.Reasons.Select(reason => reason.ToString()).ShouldNotContain("HostedServicesNotReady");
            result.Readiness.Reasons.Select(reason => reason.ToString()).ShouldNotContain("HostedServiceObservationIncomplete");
            if (networkCondition is NetworkRuntimeCondition.Present)
                result.Readiness.Reasons.ShouldBeEmpty();
        }
        else
            result.Readiness.Reasons.Select(reason => reason.ToString()).ShouldContain(serviceCondition is WorkspaceRuntimeReadinessCondition.NotReady
                ? "HostedServicesNotReady" : "HostedServiceObservationIncomplete");
        if (networkCondition is not NetworkRuntimeCondition.Present)
            result.Readiness.Reasons.ShouldContain(networkCondition is NetworkRuntimeCondition.Missing
                ? WorkspaceRuntimeReadinessReason.NetworkNotReady : WorkspaceRuntimeReadinessReason.NetworkObservationIncomplete);
        state.RecoveryCondition.ShouldBe(originalCondition);
        state.RuntimeInstanceId.ShouldBe(originalInstance);
        journal.ReceivedCalls().ShouldBeEmpty();
        await services.DidNotReceive().RestoreAsync(Arg.Any<WorkspaceHostedServices>(), Arg.Any<string>(),
            Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObserveAsync_AuthorityDeniedBeforeOrAfterServiceProbe_DeniesResult(bool afterProbe)
    {
        var runtime = Runtime();
        var state = State(runtime);
        state.ActiveTools.Add("echo");
        var services = Substitute.For<IWorkspaceHostedServiceRecovery>();
        services.DescribeAsync(Arg.Any<WorkspaceHostedServices>(), Arg.Any<CancellationToken>())
            .Returns(new WorkspaceHostedServicePlan { BlockReason = null, Digest = new string('a', 64) });
        var permitted = afterProbe;
        services.ObserveAsync(Arg.Any<WorkspaceHostedServices>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => { permitted = false; return new WorkspaceHostedServiceObservation { Condition = WorkspaceRuntimeReadinessCondition.Ready }; });
        var authorizer = Substitute.For<ICapabilityAuthorizer>();
        authorizer.AuthorizeAsync(Arg.Any<CapabilityToken>(), WorkspaceRuntimeRecovery.ReadGrant, "readiness", Arg.Any<string>())
            .Returns(_ => permitted ? Task.CompletedTask : throw new UnauthorizedAccessException());
        var recovery = new WorkspaceRuntimeRecovery(runtime, authorizer, Substitute.For<IManagementOperationJournal>(), TimeProvider.System, services);
        await Should.ThrowAsync<UnauthorizedAccessException>(() => recovery.ObserveAsync(state, new(), TestContext.Current.CancellationToken));
        if (!afterProbe)
            services.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task ObserveAsync_CancelledDuringServiceProbe_DoesNotReturnReady()
    {
        var runtime = Runtime();
        var state = State(runtime);
        state.ActiveTools.Add("echo");
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var services = Substitute.For<IWorkspaceHostedServiceRecovery>();
        services.DescribeAsync(Arg.Any<WorkspaceHostedServices>(), Arg.Any<CancellationToken>())
            .Returns(new WorkspaceHostedServicePlan { BlockReason = null, Digest = new string('a', 64) });
        services.ObserveAsync(Arg.Any<WorkspaceHostedServices>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => { caller.Cancel(); return new WorkspaceHostedServiceObservation { Condition = WorkspaceRuntimeReadinessCondition.Ready }; });
        var recovery = new WorkspaceRuntimeRecovery(runtime, Substitute.For<ICapabilityAuthorizer>(),
            Substitute.For<IManagementOperationJournal>(), TimeProvider.System, services);
        await Should.ThrowAsync<OperationCanceledException>(() => recovery.ObserveAsync(state, new(), caller.Token));
    }
}
