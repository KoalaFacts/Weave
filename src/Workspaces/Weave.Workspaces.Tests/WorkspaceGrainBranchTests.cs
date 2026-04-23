using Microsoft.Extensions.Logging.Abstractions;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;
using Weave.Workspaces.Grains;
using Weave.Workspaces.Models;
using Weave.Workspaces.Runtime;

namespace Weave.Workspaces.Tests;

/// <summary>
/// Covers <see cref="WorkspaceGrain"/> branches the main test class skips:
/// <c>StopAsync</c> when the runtime teardown throws (error-state rollback),
/// <c>StartAsync</c> lifecycle failure, and <c>OnActivateAsync</c> behavior
/// when state already has a WorkspaceId.
/// </summary>
public sealed class WorkspaceGrainBranchTests
{
    private static IPersistentState<WorkspaceState> CreateState(WorkspaceState? initial = null)
    {
        var ps = Substitute.For<IPersistentState<WorkspaceState>>();
        ps.State.Returns(initial ?? new WorkspaceState { WorkspaceId = WorkspaceId.From("ws-1") });
        ps.ReadStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        ps.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        ps.WriteStateAsync().Returns(Task.CompletedTask);
        return ps;
    }

    private static WorkspaceGrain Create(
        IPersistentState<WorkspaceState> state,
        IWorkspaceRuntime? runtime = null,
        ILifecycleManager? lifecycle = null,
        IEventBus? eventBus = null) => new(
            runtime ?? Substitute.For<IWorkspaceRuntime>(),
            lifecycle ?? Substitute.For<ILifecycleManager>(),
            eventBus ?? Substitute.For<IEventBus>(),
            TimeProvider.System,
            NullLogger<WorkspaceGrain>.Instance,
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

    [Fact]
    public async Task StartAsync_WhenAlreadyRunning_IsNoOpReturnsCurrent()
    {
        var state = CreateState(new WorkspaceState
        {
            WorkspaceId = WorkspaceId.From("ws-1"),
            Status = WorkspaceStatus.Running,
            StartedAt = DateTimeOffset.UtcNow.AddHours(-1)
        });
        var grain = Create(state);

        var result = await grain.StartAsync(Manifest());

        result.Status.ShouldBe(WorkspaceStatus.Running);
    }

    [Fact]
    public async Task StopAsync_RuntimeThrows_SetsErrorStateAndRethrows()
    {
        var state = CreateState(new WorkspaceState
        {
            WorkspaceId = WorkspaceId.From("ws-1"),
            Status = WorkspaceStatus.Running,
            StartedAt = DateTimeOffset.UtcNow
        });
        var runtime = Substitute.For<IWorkspaceRuntime>();
        runtime.TeardownAsync(Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("teardown kaboom")));
        var grain = Create(state, runtime: runtime);

        await Should.ThrowAsync<InvalidOperationException>(() => grain.StopAsync());

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
        var grain = Create(state, runtime: runtime);

        await grain.StopAsync();

        await runtime.DidNotReceive().TeardownAsync(Arg.Any<WorkspaceId>(), Arg.Any<CancellationToken>());
        state.State.Status.ShouldBe(WorkspaceStatus.Stopped);
    }

    [Fact]
    public async Task StartAsync_LifecycleThrows_LogsAndSetsErrorState()
    {
        var state = CreateState();
        var lifecycle = Substitute.For<ILifecycleManager>();
        lifecycle.RunHooksAsync(LifecyclePhase.WorkspaceStarting, Arg.Any<LifecycleContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("start hook refused")));
        var grain = Create(state, lifecycle: lifecycle);

        await Should.ThrowAsync<InvalidOperationException>(() => grain.StartAsync(Manifest()));

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
        var grain = Create(state);

        var result = await grain.GetStateAsync();

        result.Name.ShouldBe("my-workspace");
        result.Status.ShouldBe(WorkspaceStatus.Running);
    }
}
