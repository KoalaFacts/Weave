using System.Collections.Concurrent;
using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Weave.Invocations.Processes;
using Weave.Tools.Tests.Processes;

namespace Weave.Tools.Tests;

public sealed partial class ProcessRunnerTests
{
    [Fact]
    public async Task Observe_ClockAdvancesDuringExecution_PreservesAdmissionAndReturnsOwnedSnapshots()
    {
        var clock = new FakeTimeProvider();
        var runner = CreateRunner(clock);
        using var child = new ProcessTestChild("wait");
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = runner.RunAsync("node", child.Arguments, abort.Token);
        try
        {
            await child.GetProcessAsync(running);
            var initial = runner.Observe().Executions.ShouldHaveSingleItem();
            initial.Elapsed.ShouldBe(TimeSpan.Zero);
            initial.AdmittedAt.ShouldBe(clock.GetUtcNow());
            clock.Advance(TimeSpan.FromSeconds(12));
            var later = runner.Observe();
            later.ObservedAt.ShouldBe(clock.GetUtcNow());
            var execution = later.Executions.ShouldHaveSingleItem();
            execution.ExecutionId.ShouldBe(initial.ExecutionId);
            execution.AdmittedAt.ShouldBe(initial.AdmittedAt);
            execution.Elapsed.ShouldBe(TimeSpan.FromSeconds(12));
            initial.Elapsed.ShouldBe(TimeSpan.Zero);
        }
        finally
        {
            await abort.CancelAsync();
            var canceled = await Should.ThrowAsync<OperationCanceledException>(() =>
                running.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
            canceled.CancellationToken.ShouldBe(abort.Token);
        }
        runner.Observe().Available.ShouldBe(8);
        runner.Observe().Executions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Observe_StartFails_DoesNotRetainAnExecutionOrOccupyCapacity()
    {
        var runner = CreateRunner();
        await Should.ThrowAsync<Win32Exception>(() => runner.RunAsync("weave-missing-" + Guid.NewGuid().ToString("N"),
            [], TestContext.Current.CancellationToken));
        runner.Observe().Executions.ShouldBeEmpty();
        runner.Observe().Available.ShouldBe(8);
        var probe = await runner.RunAsync("dotnet", ["--version"], TestContext.Current.CancellationToken);
        probe.Failure.ShouldBe(ProcessFailure.None);
    }

    [Fact]
    public async Task Observe_ConcurrentCancellation_ReturnsConsistentCapacityWithoutMutatingSnapshots()
    {
        var runner = CreateRunner();
        using var child = new ProcessTestChild("wait");
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = runner.RunAsync("node", child.Arguments, abort.Token);
        Task[] observations = [];
        try
        {
            await child.GetProcessAsync(running);
            var before = runner.Observe();
            before.Available.ShouldBe(7);
            before.Executions.ShouldHaveSingleItem().Phase.ShouldBe(ProcessExecutionPhase.Running);
            observations = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
            {
                for (var index = 0; index < 100; index++)
                {
                    var snapshot = runner.Observe();
                    snapshot.Available.ShouldBeInRange(7, 8);
                    (snapshot.Available + snapshot.Executions.Length).ShouldBe(8);
                    await Task.Yield();
                }
            }, TestContext.Current.CancellationToken)).ToArray();
            await abort.CancelAsync();
            var canceled = await Should.ThrowAsync<OperationCanceledException>(() =>
                running.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
            canceled.CancellationToken.ShouldBe(abort.Token);
            await Task.WhenAll(observations);
            runner.Observe().Available.ShouldBe(8);
            runner.Observe().Executions.ShouldBeEmpty();
            before.Available.ShouldBe(7);
            before.Executions.ShouldHaveSingleItem().Phase.ShouldBe(ProcessExecutionPhase.Running);
        }
        finally
        {
            await abort.CancelAsync();
            try
            {
                await running.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            }
            catch (OperationCanceledException error) when (abort.IsCancellationRequested)
            {
                error.CancellationToken.ShouldBe(abort.Token);
            }
            await Task.WhenAll(observations);
        }
    }

    private static async Task WaitForAvailableAsync(ProcessRunner runner, int available)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (runner.Observe().Available != available)
            await Task.Delay(10, deadline.Token);
    }

    private sealed class ObservationDiagnostics : ILogger<ProcessRunner>
    {
        public ConcurrentQueue<Guid> ExecutionIds { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> fields)
                foreach (var field in fields)
                    if (field.Key == "ExecutionId" && field.Value is Guid id)
                        ExecutionIds.Enqueue(id);
        }
    }
}
