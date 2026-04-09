using Microsoft.Extensions.Logging;
using Weave.Shared;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;
using Weave.Workspaces.Events;
using Weave.Workspaces.Grains;
using Weave.Workspaces.Models;
using Weave.Workspaces.Runtime;

namespace Weave.Workspaces.Tests;

public sealed class WorkspaceGrainTests
{
    private static IPersistentState<WorkspaceState> CreatePersistentState(WorkspaceState? state = null)
    {
        var persistentState = Substitute.For<IPersistentState<WorkspaceState>>();
        persistentState.State.Returns(state ?? new WorkspaceState
        {
            WorkspaceId = WorkspaceId.From("test-workspace")
        });
        persistentState.ReadStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        persistentState.WriteStateAsync().Returns(Task.CompletedTask);
        persistentState.ClearStateAsync().Returns(Task.CompletedTask);
        return persistentState;
    }

    private static WorkspaceManifest CreateManifest() => new()
    {
        Name = "test-workspace",
        Version = "1.0",
        Workspace = new WorkspaceConfig
        {
            Network = new NetworkConfig { Name = "weave-test" }
        },
        Agents = new Dictionary<string, AgentDefinition>
        {
            ["researcher"] = new() { Model = "claude-sonnet-4-20250514", Tools = ["web-search"] }
        },
        Tools = new Dictionary<string, ToolDefinition>
        {
            ["web-search"] = new() { Type = "mcp" }
        }
    };

    private static (WorkspaceGrain Grain, IWorkspaceRuntime Runtime, ILifecycleManager Lifecycle, IEventBus EventBus) CreateGrain()
    {
        var runtime = Substitute.For<IWorkspaceRuntime>();
        var lifecycle = Substitute.For<ILifecycleManager>();
        var eventBus = Substitute.For<IEventBus>();
        var logger = Substitute.For<ILogger<WorkspaceGrain>>();
        var persistentState = CreatePersistentState();

        runtime.ProvisionAsync(Arg.Any<WorkspaceManifest>(), Arg.Any<CancellationToken>())
            .Returns(new WorkspaceEnvironment(
                WorkspaceId.From("test-workspace"),
                NetworkId.From("net-1"),
                [
                    new ContainerHandle(ContainerId.From("c-1"), "silo", "weave-silo:latest", new Dictionary<int, int> { [WeavePorts.SiloHttp] = WeavePorts.SiloHttp }),
                    new ContainerHandle(ContainerId.From("c-2"), "redis", "redis:7-alpine", new Dictionary<int, int> { [WeavePorts.Redis] = WeavePorts.Redis })
                ]));

        var grain = new WorkspaceGrain(runtime, lifecycle, eventBus, logger, persistentState);
        return (grain, runtime, lifecycle, eventBus);
    }

    [Fact]
    public async Task StartAsync_TransitionsToRunning()
    {
        var (grain, _, _, _) = CreateGrain();

        var state = await grain.StartAsync(CreateManifest());

        state.Status.ShouldBe(WorkspaceStatus.Running);
        state.StartedAt.ShouldNotBeNull();
        state.Containers.Count.ShouldBe(2);
    }

    [Fact]
    public async Task StartAsync_ProvisionsCalls()
    {
        var (grain, runtime, _, _) = CreateGrain();

        await grain.StartAsync(CreateManifest());

        await runtime.Received(1).ProvisionAsync(Arg.Any<WorkspaceManifest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_RunsLifecycleHooks()
    {
        var (grain, _, lifecycle, _) = CreateGrain();

        await grain.StartAsync(CreateManifest());

        await lifecycle.Received(1).RunHooksAsync(
            LifecyclePhase.WorkspaceStarting,
            Arg.Any<LifecycleContext>(),
            Arg.Any<CancellationToken>());
        await lifecycle.Received(1).RunHooksAsync(
            LifecyclePhase.WorkspaceStarted,
            Arg.Any<LifecycleContext>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_PublishesEvent()
    {
        var (grain, _, _, eventBus) = CreateGrain();

        await grain.StartAsync(CreateManifest());

        await eventBus.Received(1).PublishAsync(
            Arg.Is<WorkspaceStartedEvent>(e => e.WorkspaceName == "test-workspace"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_WhenAlreadyRunning_ReturnsCurrentState()
    {
        var (grain, runtime, _, _) = CreateGrain();
        await grain.StartAsync(CreateManifest());

        var state = await grain.StartAsync(CreateManifest());

        state.Status.ShouldBe(WorkspaceStatus.Running);
        await runtime.Received(1).ProvisionAsync(Arg.Any<WorkspaceManifest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopAsync_TransitionsToStopped()
    {
        var (grain, _, _, _) = CreateGrain();
        await grain.StartAsync(CreateManifest());

        await grain.StopAsync();

        var state = await grain.GetStateAsync();
        state.Status.ShouldBe(WorkspaceStatus.Stopped);
        state.StoppedAt.ShouldNotBeNull();
        state.Containers.ShouldBeEmpty();
        state.NetworkId.ShouldBeNull();
    }

    [Fact]
    public async Task StopAsync_RunsLifecycleHooks()
    {
        var (grain, _, lifecycle, _) = CreateGrain();
        await grain.StartAsync(CreateManifest());

        await grain.StopAsync();

        await lifecycle.Received(1).RunHooksAsync(
            LifecyclePhase.WorkspaceStopping,
            Arg.Any<LifecycleContext>(),
            Arg.Any<CancellationToken>());
        await lifecycle.Received(1).RunHooksAsync(
            LifecyclePhase.WorkspaceStopped,
            Arg.Any<LifecycleContext>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopAsync_PublishesEvent()
    {
        var (grain, _, _, eventBus) = CreateGrain();
        await grain.StartAsync(CreateManifest());

        await grain.StopAsync();

        await eventBus.Received(1).PublishAsync(
            Arg.Any<WorkspaceStoppedEvent>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopAsync_CallsTeardown()
    {
        var (grain, runtime, _, _) = CreateGrain();
        await grain.StartAsync(CreateManifest());

        await grain.StopAsync();

        await runtime.Received(1).TeardownAsync(Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopAsync_WhenNotRunning_IsNoOp()
    {
        var (grain, runtime, _, _) = CreateGrain();

        await grain.StopAsync();

        await runtime.DidNotReceive().TeardownAsync(Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_WhenProvisionFails_SetsErrorStatus()
    {
        var runtime = Substitute.For<IWorkspaceRuntime>();
        var lifecycle = Substitute.For<ILifecycleManager>();
        var eventBus = Substitute.For<IEventBus>();
        var logger = Substitute.For<ILogger<WorkspaceGrain>>();
        var persistentState = CreatePersistentState();

        runtime.ProvisionAsync(Arg.Any<WorkspaceManifest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<WorkspaceEnvironment>(new InvalidOperationException("Provisioning failed")));

        var grain = new WorkspaceGrain(runtime, lifecycle, eventBus, logger, persistentState);

        await Should.ThrowAsync<InvalidOperationException>(() => grain.StartAsync(CreateManifest()));

        var state = await grain.GetStateAsync();
        state.Status.ShouldBe(WorkspaceStatus.Error);
        state.ErrorMessage.ShouldNotBeNull();
        state.ErrorMessage.ShouldContain("Provisioning failed");
    }

    [Fact]
    public async Task GetStateAsync_ReturnsCurrentState()
    {
        var (grain, _, _, _) = CreateGrain();

        var state = await grain.GetStateAsync();

        state.ShouldNotBeNull();
        state.Status.ShouldBe(WorkspaceStatus.Stopped);
    }
}
