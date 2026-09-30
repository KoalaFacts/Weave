using Weave.Management;
using Weave.Security.Tokens;
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
        services.ReceivedCalls().ShouldBeEmpty();
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
