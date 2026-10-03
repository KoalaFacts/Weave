using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Weave.Invocations.Processes;
using Weave.Tools.Tests.Processes;

namespace Weave.Tools.Tests;

public sealed class ProcessRunnerTests
{
    private static ProcessRunner CreateRunner(TimeProvider? clock = null) =>
        new(clock ?? TimeProvider.System, NullLogger<ProcessRunner>.Instance);

    [Fact]
    public async Task RunAsync_DualLargeStreams_CapturesBothWithoutDeadlock()
    {
        using var child = new ProcessTestChild("dual");
        var result = await CreateRunner().RunAsync("node", child.Arguments, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        result.ExitCode.ShouldBe(0);
        result.Failure.ShouldBe(ProcessFailure.None);
        result.StandardOutput.ShouldBe(new string('o', 50000));
        result.StandardError.ShouldBe(new string('e', 50000));
    }

    [Fact]
    public async Task RunAsync_AlreadyCancelled_DoesNotStartChild()
    {
        using var child = new ProcessTestChild("wait");
        using var abort = new CancellationTokenSource();
        await abort.CancelAsync();
        var cancelled = await Should.ThrowAsync<OperationCanceledException>(() =>
            CreateRunner().RunAsync("node", child.Arguments, abort.Token));
        cancelled.CancellationToken.ShouldBe(abort.Token);
        File.Exists(child.PidPath).ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_OverflowBeforeExit_TerminatesChildWithoutWaitingForItsSleep()
    {
        using var child = new ProcessTestChild("overflow-wait");
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        abort.CancelAfter(TimeSpan.FromSeconds(30));
        var running = CreateRunner().RunAsync("node", child.Arguments, abort.Token);
        try
        {
            var actual = await child.GetProcessAsync(running);
            child.Release();
            var result = await running.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            result.Failure.ShouldBe(ProcessFailure.OutputLimitExceeded);
            result.StandardOutput.ShouldBeEmpty();
            result.StandardError.ShouldBeEmpty();
            await actual.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            actual.HasExited.ShouldBeTrue();
        }
        finally
        {
            await abort.CancelAsync();
            try
            {
                await running;
            }
            catch (OperationCanceledException error) when (abort.IsCancellationRequested)
            {
                error.CancellationToken.ShouldBe(abort.Token);
            }
        }
    }

    [Fact]
    public async Task RunAsync_DescendantKeepsPipeAfterRootExit_ReportsBoundedUnconfirmedCleanup()
    {
        using var child = new ProcessTestChild("descendant");
        var clock = new CleanupClock();
        var running = CreateRunner(clock).RunAsync("node", child.Arguments, TestContext.Current.CancellationToken);
        var actual = await child.GetProcessAsync(running);
        try
        {
            await clock.Scheduled.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            running.IsCompleted.ShouldBeFalse();
            actual.HasExited.ShouldBeFalse();
            clock.Advance(TimeSpan.FromSeconds(5));
            var error = await Should.ThrowAsync<TimeoutException>(() => running);
            error.Message.ShouldBe("Command process cleanup is unconfirmed.");
            actual.HasExited.ShouldBeFalse();
        }
        finally
        {
            if (!actual.HasExited)
                actual.Kill(entireProcessTree: true);
            await actual.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task RunAsync_AllSlotsOccupied_RejectsWithoutStartingAndRestoresCapacityAfterCancellation()
    {
        var runner = CreateRunner();
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        abort.CancelAfter(TimeSpan.FromSeconds(30));
        var children = Enumerable.Range(0, 8).Select(_ => new ProcessTestChild("wait")).ToArray();
        var running = children.Select(child => runner.RunAsync("node", child.Arguments, abort.Token)).ToArray();
        try
        {
            for (var index = 0; index < children.Length; index++)
            {
                var actual = await children[index].GetProcessAsync(running[index]);
                actual.HasExited.ShouldBeFalse();
            }
            using var denied = new ProcessTestChild("wait");
            var busy = await runner.RunAsync("node", denied.Arguments, TestContext.Current.CancellationToken);
            busy.Failure.ShouldBe(ProcessFailure.CapacityExhausted);
            busy.ExitCode.ShouldBeNull();
            File.Exists(denied.PidPath).ShouldBeFalse();
        }
        finally
        {
            await abort.CancelAsync();
            try
            {
                foreach (var task in running)
                {
                    var error = await Should.ThrowAsync<OperationCanceledException>(() => task);
                    error.CancellationToken.ShouldBe(abort.Token);
                }
            }
            finally
            {
                foreach (var child in children)
                    child.Dispose();
            }
        }
        var probe = await runner.RunAsync("dotnet", ["--version"], TestContext.Current.CancellationToken);
        probe.Failure.ShouldBe(ProcessFailure.None);
        probe.ExitCode.ShouldBe(0);
    }

    private sealed class CleanupClock : TimeProvider
    {
        private readonly FakeTimeProvider _clock = new();
        public TaskCompletionSource Scheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Advance(TimeSpan elapsed) => _clock.Advance(elapsed);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            dueTime.ShouldBe(TimeSpan.FromSeconds(5));
            var timer = _clock.CreateTimer(callback, state, dueTime, period);
            Scheduled.TrySetResult();
            return timer;
        }
    }
}
