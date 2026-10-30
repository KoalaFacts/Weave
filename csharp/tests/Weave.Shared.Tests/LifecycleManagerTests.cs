using Microsoft.Extensions.Logging;
using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;

namespace Weave.Shared.Tests;

public sealed class LifecycleManagerTests
{
    private readonly LifecycleManager _manager = new(Substitute.For<ILogger<LifecycleManager>>());

    private static LifecycleContext CreateContext() => new()
    {
        WorkspaceId = WorkspaceId.From("ws-1"),
        Phase = LifecyclePhase.WorkspaceStarting
    };

    private sealed class TestHook(LifecyclePhase phase, int order, Func<LifecycleContext, CancellationToken, Task>? action = null) : ILifecycleHook
    {
        public LifecyclePhase Phase => phase;
        public int Order => order;
        public List<LifecyclePhase> Invocations { get; } = [];

        public async Task ExecuteAsync(LifecycleContext context, CancellationToken ct)
        {
            Invocations.Add(context.Phase);
            if (action is not null)
                await action(context, ct);
        }
    }

    [Fact]
    public async Task RunHooksAsync_ExecutesRegisteredHook()
    {
        var hook = new TestHook(LifecyclePhase.WorkspaceStarting, 0);
        _manager.Register(hook);

        await _manager.RunHooksAsync(LifecyclePhase.WorkspaceStarting, CreateContext(), CancellationToken.None);

        hook.Invocations.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RunHooksAsync_OnlyRunsMatchingPhase()
    {
        var startHook = new TestHook(LifecyclePhase.WorkspaceStarting, 0);
        var stopHook = new TestHook(LifecyclePhase.WorkspaceStopping, 0);
        _manager.Register(startHook);
        _manager.Register(stopHook);

        await _manager.RunHooksAsync(LifecyclePhase.WorkspaceStarting, CreateContext(), CancellationToken.None);

        startHook.Invocations.Count.ShouldBe(1);
        stopHook.Invocations.ShouldBeEmpty();
    }

    [Fact]
    public async Task RunHooksAsync_ExecutesInOrder()
    {
        var executionOrder = new List<int>();
        var hook1 = new TestHook(LifecyclePhase.WorkspaceStarting, 2, (_, _) => { executionOrder.Add(2); return Task.CompletedTask; });
        var hook2 = new TestHook(LifecyclePhase.WorkspaceStarting, 1, (_, _) => { executionOrder.Add(1); return Task.CompletedTask; });
        var hook3 = new TestHook(LifecyclePhase.WorkspaceStarting, 3, (_, _) => { executionOrder.Add(3); return Task.CompletedTask; });
        _manager.Register(hook1);
        _manager.Register(hook2);
        _manager.Register(hook3);

        await _manager.RunHooksAsync(LifecyclePhase.WorkspaceStarting, CreateContext(), CancellationToken.None);

        executionOrder.ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task Register_ReturnsDisposable_ThatUnregistersHook()
    {
        var hook = new TestHook(LifecyclePhase.WorkspaceStarting, 0);
        var registration = _manager.Register(hook);

        registration.Dispose();

        await _manager.RunHooksAsync(LifecyclePhase.WorkspaceStarting, CreateContext(), CancellationToken.None);
        hook.Invocations.ShouldBeEmpty();
    }

    [Fact]
    public async Task RunHooksAsync_WithCancellation_ThrowsOperationCanceled()
    {
        var hook = new TestHook(LifecyclePhase.WorkspaceStarting, 0);
        _manager.Register(hook);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => _manager.RunHooksAsync(LifecyclePhase.WorkspaceStarting, CreateContext(), cts.Token));
    }

    [Fact]
    public async Task RunHooksAsync_WhenHookThrows_Propagates()
    {
        var hook = new TestHook(LifecyclePhase.WorkspaceStarting, 0, (_, _) =>
            throw new InvalidOperationException("hook failed"));
        _manager.Register(hook);

        await Should.ThrowAsync<InvalidOperationException>(
            () => _manager.RunHooksAsync(LifecyclePhase.WorkspaceStarting, CreateContext(), CancellationToken.None));
    }

    [Fact]
    public async Task RunHooksAsync_WithNoHooks_CompletesSuccessfully()
    {
        await _manager.RunHooksAsync(LifecyclePhase.WorkspaceStarting, CreateContext(), CancellationToken.None);

        // No exception = success
    }

    [Fact]
    public async Task RunHooksAsync_MultiplePhases_RunsCorrectHooks()
    {
        var startingHook = new TestHook(LifecyclePhase.WorkspaceStarting, 0);
        var startedHook = new TestHook(LifecyclePhase.WorkspaceStarted, 0);
        _manager.Register(startingHook);
        _manager.Register(startedHook);

        await _manager.RunHooksAsync(LifecyclePhase.WorkspaceStarting, CreateContext(), CancellationToken.None);
        await _manager.RunHooksAsync(LifecyclePhase.WorkspaceStarted, CreateContext() with { Phase = LifecyclePhase.WorkspaceStarted }, CancellationToken.None);

        startingHook.Invocations.Count.ShouldBe(1);
        startedHook.Invocations.Count.ShouldBe(1);
    }
}
