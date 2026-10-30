using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Weave.Shared.Ids;
using Weave.Shared.VirtualActors;
using Weave.Silo.Plugins;
using Weave.Tools.InstallDaprTool;
using Weave.Tools.InstallMcpTool;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Registry;

namespace Weave.Silo.Tests.Plugins;

public sealed class ToolInstallationReadinessMonitorTests
{
    private sealed class WaitingPeerProbe : IToolInstallationPeerProbe
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<InstallationFailureCode> Result { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<InstallationFailureCode> ProbeAsync(DaprToolInstallation installation,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return Result.Task;
        }

        public Task<InstallationFailureCode> ProbeAsync(McpToolInstallation installation,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected MCP probe.");
    }

    [Fact]
    public async Task ExecuteAsync_IntervalElapsed_ProbesEnabledInstallation()
    {
        const string workspaceId = "scheduled-workspace";
        var installation = new DaprToolInstallation
        {
            Id = $"{workspaceId}/sidecar",
            PluginName = "sidecar",
            DefinitionRevision = DaprToolInstallation.ImplementationRevision,
            ConfigDigest = DaprToolInstallation.ComputeConfigDigest(3500),
            DesiredEnabled = true,
            Port = 3500
        };
        var state = new WorkspaceState
        {
            WorkspaceId = WorkspaceId.From(workspaceId),
            Status = WorkspaceStatus.Running,
            DaprToolInstallations = [installation]
        };
        var registry = Substitute.For<IWorkspaceRegistryActor>();
        registry.GetWorkspaceIdsAsync().Returns(Task.FromResult<IReadOnlyList<string>>([workspaceId]));
        var workspace = Substitute.For<IWorkspaceActor>();
        workspace.GetStateAsync().Returns(Task.FromResult(state));
        var actors = Substitute.For<IVirtualActorProvider>();
        actors.GetActor<IWorkspaceRegistryActor>(Arg.Any<VirtualActorId>()).Returns(registry);
        actors.GetActor<IWorkspaceActor>(Arg.Any<VirtualActorId>()).Returns(workspace);
        using var applicationStarted = new CancellationTokenSource();
        applicationStarted.Cancel();
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStarted.Returns(applicationStarted.Token);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero));
        var peer = new WaitingPeerProbe();
        var diagnostics = new InstallationDiagnostics(clock);
        using var monitor = new ToolInstallationReadinessMonitor(lifetime, actors, peer, diagnostics, clock,
            NullLogger<ToolInstallationReadinessMonitor>.Instance);

        await monitor.StartAsync(TestContext.Current.CancellationToken);
        await monitor.TimerReady.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        peer.Started.Task.IsCompleted.ShouldBeFalse();
        clock.Advance(TimeSpan.FromSeconds(30));
        await peer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        peer.Result.TrySetResult(InstallationFailureCode.None);
        await monitor.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ProbeOnceAsync_InstallationDisabledDuringProbe_DiscardsStaleResult()
    {
        const string workspaceId = "probe-workspace";
        var installation = new DaprToolInstallation
        {
            Id = $"{workspaceId}/sidecar",
            PluginName = "sidecar",
            DefinitionRevision = DaprToolInstallation.ImplementationRevision,
            ConfigDigest = DaprToolInstallation.ComputeConfigDigest(3500),
            DesiredEnabled = true,
            Port = 3500
        };
        WorkspaceState current = new()
        {
            WorkspaceId = WorkspaceId.From(workspaceId),
            Status = WorkspaceStatus.Running,
            DaprToolInstallations = [installation]
        };
        var registry = Substitute.For<IWorkspaceRegistryActor>();
        registry.GetWorkspaceIdsAsync().Returns(Task.FromResult<IReadOnlyList<string>>([workspaceId]));
        var workspace = Substitute.For<IWorkspaceActor>();
        workspace.GetStateAsync().Returns(_ => Task.FromResult(current));
        var actors = Substitute.For<IVirtualActorProvider>();
        actors.GetActor<IWorkspaceRegistryActor>(Arg.Any<VirtualActorId>()).Returns(registry);
        actors.GetActor<IWorkspaceActor>(Arg.Any<VirtualActorId>()).Returns(workspace);
        var peer = new WaitingPeerProbe();
        var diagnostics = new InstallationDiagnostics(TimeProvider.System);
        using var monitor = new ToolInstallationReadinessMonitor(Substitute.For<IHostApplicationLifetime>(),
            actors, peer, diagnostics, TimeProvider.System,
            NullLogger<ToolInstallationReadinessMonitor>.Instance);

        var probing = monitor.ProbeOnceAsync(TestContext.Current.CancellationToken);
        await peer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        current = current with
        {
            DaprToolInstallations = [installation with { DesiredEnabled = false }]
        };
        peer.Result.TrySetResult(InstallationFailureCode.None);
        await probing;

        diagnostics.GetProbe(installation).ShouldBeNull();

        current = current with { DaprToolInstallations = [installation] };
        var cancelledPeer = new WaitingPeerProbe();
        using var cancelledMonitor = new ToolInstallationReadinessMonitor(
            Substitute.For<IHostApplicationLifetime>(), actors, cancelledPeer, diagnostics,
            TimeProvider.System, NullLogger<ToolInstallationReadinessMonitor>.Instance);
        using var cancellation = new CancellationTokenSource();
        var cancelledProbe = cancelledMonitor.ProbeOnceAsync(cancellation.Token);
        await cancelledPeer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        cancelledPeer.Result.TrySetResult(InstallationFailureCode.None);
        await Should.ThrowAsync<OperationCanceledException>(async () => await cancelledProbe);
        diagnostics.GetProbe(installation).ShouldBeNull();
    }

    [Fact]
    public async Task ProbeOnceAsync_InvalidStoredInstallations_DoNotContactPeers()
    {
        const string workspaceId = "invalid-workspace";
        var dapr = new DaprToolInstallation
        {
            Id = $"{workspaceId}/sidecar",
            PluginName = "sidecar",
            DefinitionRevision = "unknown-revision",
            ConfigDigest = DaprToolInstallation.ComputeConfigDigest(3500),
            DesiredEnabled = true,
            Port = 3500
        };
        const string remoteUrl = "http://example.com/mcp";
        var mcp = new McpToolInstallation
        {
            Id = $"{workspaceId}/peer",
            PluginName = "peer",
            DefinitionRevision = McpToolInstallation.ImplementationRevision,
            ConfigDigest = McpToolInstallation.ComputeConfigDigest(remoteUrl, "peer", "1.0", "echo"),
            DesiredEnabled = true,
            Url = remoteUrl,
            ServerName = "peer",
            ServerVersion = "1.0",
            Operation = "echo",
            ContractDigest = new string('A', 64)
        };
        var state = new WorkspaceState
        {
            WorkspaceId = WorkspaceId.From(workspaceId),
            Status = WorkspaceStatus.Running,
            DaprToolInstallations = [dapr],
            McpToolInstallations = [mcp]
        };
        var registry = Substitute.For<IWorkspaceRegistryActor>();
        registry.GetWorkspaceIdsAsync().Returns(Task.FromResult<IReadOnlyList<string>>([workspaceId]));
        var workspace = Substitute.For<IWorkspaceActor>();
        workspace.GetStateAsync().Returns(Task.FromResult(state));
        var actors = Substitute.For<IVirtualActorProvider>();
        actors.GetActor<IWorkspaceRegistryActor>(Arg.Any<VirtualActorId>()).Returns(registry);
        actors.GetActor<IWorkspaceActor>(Arg.Any<VirtualActorId>()).Returns(workspace);
        var peer = new WaitingPeerProbe();
        var diagnostics = new InstallationDiagnostics(TimeProvider.System);
        using var monitor = new ToolInstallationReadinessMonitor(Substitute.For<IHostApplicationLifetime>(),
            actors, peer, diagnostics, TimeProvider.System,
            NullLogger<ToolInstallationReadinessMonitor>.Instance);

        await monitor.ProbeOnceAsync(TestContext.Current.CancellationToken);

        peer.Started.Task.IsCompleted.ShouldBeFalse();
        diagnostics.GetProbe(dapr).ShouldBeNull();
        diagnostics.GetProbe(mcp).ShouldBeNull();
    }

    [Fact]
    public async Task ProbeOnceAsync_WorkspaceOrProbeFails_ContinuesWithoutInventingObservation()
    {
        const string workspaceId = "healthy-workspace";
        var installation = new DaprToolInstallation
        {
            Id = $"{workspaceId}/sidecar",
            PluginName = "sidecar",
            DefinitionRevision = DaprToolInstallation.ImplementationRevision,
            ConfigDigest = DaprToolInstallation.ComputeConfigDigest(3500),
            DesiredEnabled = true,
            Port = 3500
        };
        var state = new WorkspaceState
        {
            WorkspaceId = WorkspaceId.From(workspaceId),
            Status = WorkspaceStatus.Running,
            DaprToolInstallations = [installation]
        };
        var registry = Substitute.For<IWorkspaceRegistryActor>();
        registry.GetWorkspaceIdsAsync().Returns(Task.FromResult<IReadOnlyList<string>>(["failed-workspace", workspaceId]));
        var failed = Substitute.For<IWorkspaceActor>();
        failed.GetStateAsync().Returns(Task.FromException<WorkspaceState>(new InvalidOperationException("Unavailable")));
        var healthy = Substitute.For<IWorkspaceActor>();
        healthy.GetStateAsync().Returns(Task.FromResult(state));
        var actors = Substitute.For<IVirtualActorProvider>();
        actors.GetActor<IWorkspaceRegistryActor>(Arg.Any<VirtualActorId>()).Returns(registry);
        actors.GetActor<IWorkspaceActor>(VirtualActorId.From("failed-workspace")).Returns(failed);
        actors.GetActor<IWorkspaceActor>(VirtualActorId.From(workspaceId)).Returns(healthy);
        var peer = new WaitingPeerProbe();
        peer.Result.TrySetResult(InstallationFailureCode.None);
        var diagnostics = new InstallationDiagnostics(TimeProvider.System);
        using var monitor = new ToolInstallationReadinessMonitor(Substitute.For<IHostApplicationLifetime>(),
            actors, peer, diagnostics, TimeProvider.System,
            NullLogger<ToolInstallationReadinessMonitor>.Instance);

        await monitor.ProbeOnceAsync(TestContext.Current.CancellationToken);

        peer.Started.Task.IsCompleted.ShouldBeTrue();
        diagnostics.GetProbe(installation)?.Failure.ShouldBe(InstallationFailureCode.None);

        diagnostics.ClearProbe(installation.Id);
        var faultyPeer = new WaitingPeerProbe();
        faultyPeer.Result.TrySetException(new ArgumentException("Unexpected probe defect"));
        using var faultyMonitor = new ToolInstallationReadinessMonitor(Substitute.For<IHostApplicationLifetime>(),
            actors, faultyPeer, diagnostics, TimeProvider.System,
            NullLogger<ToolInstallationReadinessMonitor>.Instance);

        await faultyMonitor.ProbeOnceAsync(TestContext.Current.CancellationToken);

        faultyPeer.Started.Task.IsCompleted.ShouldBeTrue();
        diagnostics.GetProbe(installation).ShouldBeNull();
    }
}
