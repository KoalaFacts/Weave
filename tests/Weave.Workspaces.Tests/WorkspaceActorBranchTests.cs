using Microsoft.Extensions.Logging.Abstractions;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;
using Weave.Tools.Connectors;
using Weave.Tools.InstallDaprTool;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.Registry;
using Weave.Workspaces.Runtime;
using Weave.Workspaces.Templates;

namespace Weave.Workspaces.Tests;

/// <summary>
/// Covers <see cref="WorkspaceActor"/> branches the main test class skips:
/// <c>StopAsync</c> when the runtime teardown throws (error-state rollback),
/// <c>StartAsync</c> lifecycle failure, and <c>OnActivateAsync</c> behavior
/// when state already has a WorkspaceId.
/// </summary>
public sealed class WorkspaceActorBranchTests
{
    private static IActorState<WorkspaceState> CreateState(WorkspaceState? initial = null)
    {
        var ps = Substitute.For<IActorState<WorkspaceState>>();
        ps.State.Returns(initial ?? new WorkspaceState { WorkspaceId = WorkspaceId.From("ws-1") });
        ps.ReadStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        ps.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        ps.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return ps;
    }

    private static WorkspaceActor Create(
        IActorState<WorkspaceState> state,
        IWorkspaceRuntime? runtime = null,
        ILifecycleManager? lifecycle = null,
        IEventBus? eventBus = null) => new(
            runtime ?? Substitute.For<IWorkspaceRuntime>(),
            lifecycle ?? Substitute.For<ILifecycleManager>(),
            eventBus ?? Substitute.For<IEventBus>(),
            TimeProvider.System,
            NullLogger<WorkspaceActor>.Instance,
            state);

    private static WorkspaceManifest Manifest() => new()
    {
        Version = "1.0",
        Name = "test",
        Agents = new Dictionary<string, AgentDefinition>
        {
            ["a"] = new() { Model = "m" }
        }
    };

    private static WorkspaceManifest DaprManifest(string pluginName) => new()
    {
        Version = "1.0",
        Name = "dapr-test",
        Plugins = new Dictionary<string, PluginDefinition>
        {
            [pluginName] = new()
            {
                Type = "dapr_tools",
                Config = new Dictionary<string, string> { ["port"] = "3500" }
            }
        },
        Tools = new Dictionary<string, ToolDefinition>
        {
            ["echo"] = new()
            {
                Type = "dapr",
                RequiresPlugin = pluginName,
                Dapr = new DaprToolConfig { AppId = "echo-service" }
            }
        }
    };

    [Fact]
    public async Task StartAsync_CaseChangedDaprInstallation_RejectsBeforeProvisioning()
    {
        var state = CreateState(new WorkspaceState
        {
            WorkspaceId = WorkspaceId.From("ws-1"),
            Status = WorkspaceStatus.Stopped,
            DaprToolInstallations =
            [
                new DaprToolInstallation
                {
                    Id = "ws-1/sidecar", PluginName = "sidecar", Port = 3500,
                    ConfigDigest = DaprToolInstallation.ComputeConfigDigest(3500)
                }
            ]
        });
        var runtime = Substitute.For<IWorkspaceRuntime>();
        runtime.ProvisionAsync(Arg.Any<WorkspaceId>(), Arg.Any<WorkspaceManifest>(), Arg.Any<CancellationToken>())
            .Returns(new WorkspaceEnvironment(WorkspaceId.From("ws-1"), NetworkId.From("net-1"), []));
        var actor = Create(state, runtime: runtime);

        var error = await Should.ThrowAsync<InvalidOperationException>(() => actor.StartAsync(DaprManifest("SIDECAR")));

        error.Message.ShouldContain("differs in case");
        state.State.Status.ShouldBe(WorkspaceStatus.Stopped);
        state.State.DaprToolInstallations.Count.ShouldBe(1);
        await runtime.DidNotReceive().ProvisionAsync(Arg.Any<WorkspaceId>(), Arg.Any<WorkspaceManifest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_AmbiguousStoredDaprInstallation_RejectsBeforeProvisioning()
    {
        var state = CreateState(new WorkspaceState
        {
            WorkspaceId = WorkspaceId.From("ws-1"),
            Status = WorkspaceStatus.Stopped,
            DaprToolInstallations =
            [
                new DaprToolInstallation { Id = "ws-1/sidecar", PluginName = "sidecar" },
                new DaprToolInstallation { Id = "ws-1/SIDECAR", PluginName = "SIDECAR" }
            ]
        });
        var runtime = Substitute.For<IWorkspaceRuntime>();
        runtime.ProvisionAsync(Arg.Any<WorkspaceId>(), Arg.Any<WorkspaceManifest>(), Arg.Any<CancellationToken>())
            .Returns(new WorkspaceEnvironment(WorkspaceId.From("ws-1"), NetworkId.From("net-1"), []));
        var actor = Create(state, runtime: runtime);

        var error = await Should.ThrowAsync<InvalidOperationException>(() => actor.StartAsync(DaprManifest("sidecar")));

        error.Message.ShouldContain("ambiguous");
        state.State.Status.ShouldBe(WorkspaceStatus.Stopped);
        state.State.DaprToolInstallations.Count.ShouldBe(2);
        await runtime.DidNotReceive().ProvisionAsync(Arg.Any<WorkspaceId>(), Arg.Any<WorkspaceManifest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_SameDaprInstallation_ReusesStoredIdentity()
    {
        var installation = new DaprToolInstallation
        {
            Id = "ws-1/sidecar",
            PluginName = "sidecar",
            Port = 3500,
            ConfigDigest = DaprToolInstallation.ComputeConfigDigest(3500)
        };
        var state = CreateState(new WorkspaceState
        {
            WorkspaceId = WorkspaceId.From("ws-1"),
            Status = WorkspaceStatus.Stopped,
            DaprToolInstallations = [installation]
        });
        var runtime = Substitute.For<IWorkspaceRuntime>();
        runtime.ProvisionAsync(Arg.Any<WorkspaceId>(), Arg.Any<WorkspaceManifest>(), Arg.Any<CancellationToken>())
            .Returns(new WorkspaceEnvironment(WorkspaceId.From("ws-1"), NetworkId.From("net-1"), []));
        var actor = Create(state, runtime: runtime);

        await actor.StartAsync(DaprManifest("sidecar"));

        state.State.Status.ShouldBe(WorkspaceStatus.Running);
        state.State.DaprToolInstallations.ShouldHaveSingleItem().ShouldBeSameAs(installation);
        installation.DesiredEnabled.ShouldBeTrue();
        await runtime.Received(1).ProvisionAsync(Arg.Any<WorkspaceId>(), Arg.Any<WorkspaceManifest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_ChangedDaprConfiguration_RejectsBeforeProvisioning()
    {
        var state = CreateState(new WorkspaceState
        {
            WorkspaceId = WorkspaceId.From("ws-1"),
            Status = WorkspaceStatus.Stopped,
            DaprToolInstallations =
            [
                new DaprToolInstallation
                {
                    Id = "ws-1/sidecar", PluginName = "sidecar", Port = 3501,
                    ConfigDigest = DaprToolInstallation.ComputeConfigDigest(3501)
                }
            ]
        });
        var runtime = Substitute.For<IWorkspaceRuntime>();
        runtime.ProvisionAsync(Arg.Any<WorkspaceId>(), Arg.Any<WorkspaceManifest>(), Arg.Any<CancellationToken>())
            .Returns(new WorkspaceEnvironment(WorkspaceId.From("ws-1"), NetworkId.From("net-1"), []));
        var actor = Create(state, runtime: runtime);

        var error = await Should.ThrowAsync<InvalidOperationException>(() => actor.StartAsync(DaprManifest("sidecar")));

        error.Message.ShouldContain("changed");
        state.State.Status.ShouldBe(WorkspaceStatus.Stopped);
        state.State.DaprToolInstallations.ShouldHaveSingleItem().Port.ShouldBe(3501);
        await runtime.DidNotReceive().ProvisionAsync(Arg.Any<WorkspaceId>(), Arg.Any<WorkspaceManifest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_CaseCollidingDaprPluginNames_RejectsBeforeProvisioning()
    {
        var manifest = DaprManifest("sidecar");
        manifest.Plugins["SIDECAR"] = manifest.Plugins["sidecar"];
        manifest.Tools["second"] = new ToolDefinition
        {
            Type = "dapr",
            RequiresPlugin = "SIDECAR",
            Dapr = new DaprToolConfig { AppId = "echo-service" }
        };
        var state = CreateState();
        var runtime = Substitute.For<IWorkspaceRuntime>();
        runtime.ProvisionAsync(Arg.Any<WorkspaceId>(), Arg.Any<WorkspaceManifest>(), Arg.Any<CancellationToken>())
            .Returns(new WorkspaceEnvironment(WorkspaceId.From("ws-1"), NetworkId.From("net-1"), []));
        var actor = Create(state, runtime: runtime);

        var error = await Should.ThrowAsync<InvalidOperationException>(() => actor.StartAsync(manifest));

        error.Message.ShouldContain("case");
        state.State.Status.ShouldBe(WorkspaceStatus.Stopped);
        await runtime.DidNotReceive().ProvisionAsync(Arg.Any<WorkspaceId>(), Arg.Any<WorkspaceManifest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_WhenAlreadyRunning_IsNoOpReturnsCurrent()
    {
        var state = CreateState(new WorkspaceState
        {
            WorkspaceId = WorkspaceId.From("ws-1"),
            Status = WorkspaceStatus.Running,
            StartedAt = DateTimeOffset.UtcNow.AddHours(-1)
        });
        var actor = Create(state);

        var result = await actor.StartAsync(Manifest());

        result.Status.ShouldBe(WorkspaceStatus.Running);
    }

    [Fact]
    public async Task StopAsync_RuntimeThrows_SetsErrorStateAndRethrows()
    {
        var state = CreateState(new WorkspaceState
        {
            WorkspaceId = WorkspaceId.From("ws-1"),
            Status = WorkspaceStatus.Running,
            RuntimeName = "podman",
            NetworkId = NetworkId.From("net-1"),
            StartedAt = DateTimeOffset.UtcNow
        });
        var runtime = Substitute.For<IWorkspaceRuntime>();
        runtime.RuntimeName.Returns("podman");
        runtime.TeardownAsync(Arg.Any<WorkspaceId>(), Arg.Any<NetworkId?>(), Arg.Any<IReadOnlyList<ContainerId>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("teardown kaboom")));
        var actor = Create(state, runtime: runtime);

        await Should.ThrowAsync<InvalidOperationException>(() => actor.StopAsync());

        state.State.Status.ShouldBe(WorkspaceStatus.Error);
        state.State.ErrorMessage.ShouldNotBeNull();
        state.State.ErrorMessage.ShouldContain("teardown kaboom");
    }

    [Fact]
    public async Task StopAsync_WhenNotRunning_IsNoOp()
    {
        var state = CreateState(new WorkspaceState
        {
            WorkspaceId = WorkspaceId.From("ws-1"),
            Status = WorkspaceStatus.Stopped
        });
        var runtime = Substitute.For<IWorkspaceRuntime>();
        var actor = Create(state, runtime: runtime);

        await actor.StopAsync();

        await runtime.DidNotReceive().TeardownAsync(Arg.Any<WorkspaceId>(), Arg.Any<NetworkId?>(), Arg.Any<IReadOnlyList<ContainerId>>(), Arg.Any<CancellationToken>());
        state.State.Status.ShouldBe(WorkspaceStatus.Stopped);
    }

    [Fact]
    public async Task StartAsync_LifecycleThrows_LogsAndSetsErrorState()
    {
        var state = CreateState();
        var lifecycle = Substitute.For<ILifecycleManager>();
        lifecycle.RunHooksAsync(LifecyclePhase.WorkspaceStarting, Arg.Any<LifecycleContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("start hook refused")));
        var actor = Create(state, lifecycle: lifecycle);

        await Should.ThrowAsync<InvalidOperationException>(() => actor.StartAsync(Manifest()));

        state.State.Status.ShouldBe(WorkspaceStatus.Error);
    }

    [Fact]
    public async Task GetStateAsync_ReturnsPersistentState()
    {
        var state = CreateState(new WorkspaceState
        {
            WorkspaceId = WorkspaceId.From("ws-1"),
            Name = "my-workspace",
            Status = WorkspaceStatus.Running
        });
        var actor = Create(state);

        var result = await actor.GetStateAsync();

        result.Name.ShouldBe("my-workspace");
        result.Status.ShouldBe(WorkspaceStatus.Running);
    }
}
