using Weave.Silo.Plugins;
using Weave.Tools.InstallMcpTool;
using Weave.Tools.Tool;
using Weave.Workspaces.RuntimeRecovery;

namespace Weave.Silo.Tests.RuntimeRecovery;

public sealed partial class McpWorkspaceServiceRecoveryTests
{
    [Fact]
    public async Task ObserveAsync_CurrentConnectionAndPeer_ReturnsReadyWithoutRestoring()
    {
        var fx = new Fixture();
        var result = await ObserveAsync(fx);
        result.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.Ready);
        result.McpInstallations.Single().ToolName.ShouldBe("echo");
        result.McpInstallations.Single().Reason.ShouldBeNull();
        await fx.Registry.DidNotReceive().RestoreMcpToolAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Weave.Security.Tokens.CapabilityToken>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(InstallationFailureCode.PeerUnavailable, WorkspaceRuntimeReadinessCondition.NotReady, "mcp-peer-unavailable")]
    [InlineData(InstallationFailureCode.ContractRejected, WorkspaceRuntimeReadinessCondition.NotReady, "mcp-contract-rejected")]
    [InlineData(InstallationFailureCode.ConnectionFailed, WorkspaceRuntimeReadinessCondition.Unknown, "mcp-observation-incomplete")]
    public async Task ObserveAsync_PeerFails_PreservesMeaningfulCondition(InstallationFailureCode failure,
        WorkspaceRuntimeReadinessCondition condition, string reason)
    {
        var fx = new Fixture();
        fx.Probe.OnProbe = (_, _) => Task.FromResult(failure);
        var result = await ObserveAsync(fx);
        result.Condition.ShouldBe(condition);
        result.McpInstallations.Single().Reason.ShouldBe(reason);
    }

    [Fact]
    public async Task ObserveAsync_MissingRegistration_DoesNotProbePeer()
    {
        var fx = new Fixture();
        fx.Installations.MatchesInstallation(Arg.Any<McpToolInstallationSnapshot>()).Returns(false);
        fx.Probe.OnProbe = (_, _) => throw new InvalidOperationException("Must not probe an unregistered target.");
        var result = await ObserveAsync(fx);
        result.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.NotReady);
        result.McpInstallations.Single().Reason.ShouldBe("mcp-installation-not-restored");
    }

    [Fact]
    public async Task ObserveAsync_ConnectionMissing_ReportsNotReady()
    {
        var fx = new Fixture();
        fx.Connection.HasCurrentConnectionAsync(ToolType.Mcp, "ws/server", Arg.Any<CancellationToken>()).Returns(false);
        var result = await ObserveAsync(fx);
        result.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.NotReady);
        result.McpInstallations.Single().Reason.ShouldBe("mcp-tool-not-connected");
    }

    [Theory]
    [InlineData(InstallationFailureCode.ConnectionFailed, WorkspaceRuntimeReadinessCondition.Unknown)]
    [InlineData(InstallationFailureCode.PeerUnavailable, WorkspaceRuntimeReadinessCondition.NotReady)]
    public async Task ObserveAsync_MultipleInstallations_ReadyPeerDoesNotMaskAnotherFailure(
        InstallationFailureCode failure, WorkspaceRuntimeReadinessCondition condition)
    {
        var fx = new Fixture();
        var other = fx.Services.McpInstallations.Single() with
        {
            Id = "ws/other",
            PluginName = "other",
            Operation = "other_echo",
            ConfigDigest = McpToolInstallation.ComputeConfigDigest(fx.Tool.Url!, "echo", "1", "other_echo")
        };
        var services = fx.Services with
        {
            Tools = ["echo", "other_echo"],
            Plugins = ["server", "other"],
            McpInstallations = [fx.Services.McpInstallations.Single(), other]
        };
        fx.Registry.GetMcpRecoveryPlansAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { fx.Tool, fx.Tool with { Name = "other_echo", PluginName = "other", Digest = new string('e', 64) } });
        fx.Probe.OnProbe = (installation, _) => Task.FromResult(installation.Operation == "echo" ? InstallationFailureCode.None : failure);
        var plan = await fx.Recovery.DescribeAsync(services, TestContext.Current.CancellationToken);
        var result = await fx.Recovery.ObserveAsync(services, plan.Digest, TestContext.Current.CancellationToken);
        result.Condition.ShouldBe(condition);
        result.McpInstallations.Single(item => item.InstallationId == "ws/server").Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.Ready);
        result.McpInstallations.Single(item => item.InstallationId == "ws/other").Condition.ShouldBe(condition);
    }

    [Fact]
    public async Task ObserveAsync_InstallationHasNoRecordedTool_ReportsMissingContribution()
    {
        var fx = new Fixture();
        var services = fx.Services with { Tools = [] };
        fx.Registry.GetMcpRecoveryPlansAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<Weave.Agents.ToolRegistry.McpToolRecoveryPlan>());
        var plan = await fx.Recovery.DescribeAsync(services, TestContext.Current.CancellationToken);
        var result = await fx.Recovery.ObserveAsync(services, plan.Digest, TestContext.Current.CancellationToken);
        result.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.NotReady);
        result.McpInstallations.Single().Reason.ShouldBe("mcp-tool-not-recorded");
        fx.Connection.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObserveAsync_PlanChanges_DoesNotReportReady(bool duringProbe)
    {
        var fx = new Fixture();
        var digest = (await fx.Recovery.DescribeAsync(fx.Services, TestContext.Current.CancellationToken)).Digest;
        if (duringProbe)
            fx.Probe.OnProbe = (_, _) => { fx.SetTools(fx.Tool with { Digest = new string('d', 64) }); return Task.FromResult(InstallationFailureCode.None); };
        else
            fx.SetTools(fx.Tool with { Digest = new string('d', 64) });
        var result = await fx.Recovery.ObserveAsync(fx.Services, digest, TestContext.Current.CancellationToken);
        result.Condition.ShouldBe(WorkspaceRuntimeReadinessCondition.Unknown);
        result.Reason.ShouldBe("hosted-service-plan-changed");
    }

    [Fact]
    public async Task ObserveAsync_DisabledDuringProbe_DoesNotReportReady()
    {
        var fx = new Fixture();
        fx.Probe.OnProbe = (_, _) =>
        {
            fx.Installations.MatchesInstallation(Arg.Any<McpToolInstallationSnapshot>()).Returns(false);
            return Task.FromResult(InstallationFailureCode.None);
        };
        (await ObserveAsync(fx)).McpInstallations.Single().Reason.ShouldBe("mcp-installation-not-restored");
    }

    [Fact]
    public async Task ObserveAsync_CancelledProbe_PropagatesCancellation()
    {
        var fx = new Fixture();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        fx.Probe.OnProbe = (_, ct) => { caller.Cancel(); ct.ThrowIfCancellationRequested(); return Task.FromResult(InstallationFailureCode.None); };
        var digest = (await fx.Recovery.DescribeAsync(fx.Services, caller.Token)).Digest;
        await Should.ThrowAsync<OperationCanceledException>(() => fx.Recovery.ObserveAsync(fx.Services, digest, caller.Token));
    }

    private static async Task<WorkspaceHostedServiceObservation> ObserveAsync(Fixture fx) =>
        await fx.Recovery.ObserveAsync(fx.Services,
            (await fx.Recovery.DescribeAsync(fx.Services, TestContext.Current.CancellationToken)).Digest,
            TestContext.Current.CancellationToken);
}
