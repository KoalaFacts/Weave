using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Weave.Invocations.Processes;
using Weave.Security.Tokens;
using Weave.Tools.Connectors;
using Weave.Tools.Tests.Processes;
using Weave.Tools.Tool;
using Weave.Workspaces.Manifest;

namespace Weave.Tools.Tests;

[Trait("Category", "Integration")]
public sealed class CliToolConnectorCapacityAndCleanupTests
{
    [Fact]
    public async Task InvokeAsync_AllProcessSlotsOccupied_ReturnsCapacityErrorWithoutStartingCommand()
    {
        var runner = new ProcessRunner(TimeProvider.System, NullLogger<ProcessRunner>.Instance);
        var (connector, handle) = await ConnectAsync(runner);
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        abort.CancelAfter(TimeSpan.FromSeconds(30));
        var children = Enumerable.Range(0, 8).Select(_ => new ProcessTestChild("wait")).ToArray();
        var running = children.Select(child => runner.RunAsync("node", child.Arguments, abort.Token)).ToArray();
        using var denied = new ProcessTestChild("effect-overflow");
        try
        {
            for (var index = 0; index < children.Length; index++)
                await children[index].GetProcessAsync(running[index]);

            var result = await connector.InvokeAsync(handle, denied.Invocation, TestContext.Current.CancellationToken);

            result.Success.ShouldBeFalse();
            result.ToolName.ShouldBe(handle.ToolName);
            result.ErrorCode.ShouldBe("process-capacity-exhausted");
            result.Error.ShouldBe("Command process capacity is exhausted.");
            result.Output.ShouldBeEmpty();
            File.Exists(Path.Join(denied.Root, "effect")).ShouldBeFalse();
        }
        finally
        {
            await abort.CancelAsync();
            try
            {
                foreach (var task in running)
                {
                    var cancelled = await Should.ThrowAsync<OperationCanceledException>(() => task);
                    cancelled.CancellationToken.ShouldBe(abort.Token);
                }
            }
            finally
            {
                foreach (var child in children)
                    child.Dispose();
            }
        }

        using var probe = new ProcessTestChild("dual");
        var recovered = await connector.InvokeAsync(handle, probe.Invocation, TestContext.Current.CancellationToken);
        recovered.Success.ShouldBeTrue();
        recovered.Output.ShouldBe(new string('o', 50000));
        recovered.Error.ShouldBe(new string('e', 50000));
        runner.Observe().Available.ShouldBe(8);
        runner.Observe().Executions.ShouldBeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_DescendantRetainsOutputPipes_ReturnsCleanupUnconfirmedWithoutPartialOutput()
    {
        using var child = new ProcessTestChild("descendant");
        var clock = new CleanupClock();
        var runner = new ProcessRunner(clock, NullLogger<ProcessRunner>.Instance);
        var (connector, handle) = await ConnectAsync(runner);
        var running = connector.InvokeAsync(handle, child.Invocation, TestContext.Current.CancellationToken);
        var descendant = await child.GetProcessAsync(running);
        try
        {
            await clock.Scheduled.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            descendant.HasExited.ShouldBeFalse();
            running.IsCompleted.ShouldBeFalse();
            runner.Observe().Executions.ShouldHaveSingleItem().Phase.ShouldBe(ProcessExecutionPhase.CleaningUp);

            clock.Advance(TimeSpan.FromSeconds(5));
            var result = await running.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            result.Success.ShouldBeFalse();
            result.ToolName.ShouldBe(handle.ToolName);
            result.ErrorCode.ShouldBe("process-cleanup-unconfirmed");
            result.Error.ShouldBe("Command process cleanup is unconfirmed.");
            result.Output.ShouldBeEmpty();
            descendant.HasExited.ShouldBeFalse();
            runner.Observe().Executions.ShouldHaveSingleItem().Phase.ShouldBe(ProcessExecutionPhase.CleanupUnconfirmed);
        }
        finally
        {
            if (!descendant.HasExited)
                descendant.Kill(entireProcessTree: true);
            await descendant.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (runner.Observe().Executions.Length != 0)
            await Task.Delay(10, deadline.Token);
        runner.Observe().Available.ShouldBe(8);
    }

    private static async Task<(CliToolConnector Connector, ToolHandle Handle)> ConnectAsync(ProcessRunner runner)
    {
        var connector = new CliToolConnector(NullLogger<CliToolConnector>.Instance, runner);
        var handle = await connector.ConnectAsync(new ToolSpec
        {
            Name = "process-test",
            Type = ToolType.Cli,
            Cli = new CliConfig { Shell = OperatingSystem.IsWindows() ? "powershell" : "/bin/sh" }
        }, new CapabilityToken { TokenId = "test", WorkspaceId = "process-tests", Grants = [] },
            TestContext.Current.CancellationToken);
        return (connector, handle);
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
