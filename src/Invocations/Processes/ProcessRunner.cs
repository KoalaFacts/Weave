using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Weave.Invocations.Processes;

public sealed partial class ProcessRunner(TimeProvider clock, ILogger<ProcessRunner> logger) : IProcessRunner
{
    private const int MaxConcurrentProcesses = 8;
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
    private readonly Channel<byte> _slots = CreateSlots();
    private readonly ConcurrentDictionary<Process, Task> _unfinished = new();

    public async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_slots.Reader.TryRead(out _))
            return new ProcessResult(null, string.Empty, string.Empty, ProcessFailure.CapacityExhausted);
        Process? process = null;
        var retainedForCleanup = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);

            process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start command process.");
            var overflow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stdout = ProcessOutputCapture.Start(process.StandardOutput, overflow);
            var stderr = ProcessOutputCapture.Start(process.StandardError, overflow);
            var exit = process.WaitForExitAsync(CancellationToken.None);
            var completion = Task.WhenAll(exit, stdout, stderr);
            ExceptionDispatchInfo? failure = null;
            try
            {
                var finished = await Task.WhenAny(exit, overflow.Task).WaitAsync(ct);
                if (finished == overflow.Task)
                    await overflow.Task;
            }
            catch (OperationCanceledException error) when (ct.IsCancellationRequested)
            {
                failure = ExceptionDispatchInfo.Capture(error);
            }
            catch (Exception error)
            {
                LogProcessFailure(logger, error.GetType().Name);
                failure = ExceptionDispatchInfo.Capture(error);
            }
            var termination = Task.Factory.StartNew(() => KillIfRunning(process),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var cleanup = Task.WhenAll(completion, termination);
            try
            {
                await cleanup.WaitAsync(CleanupTimeout, clock, CancellationToken.None);
            }
            catch (TimeoutException)
            {
                var processId = process.Id;
                retainedForCleanup = true;
                RetainUntilCompleted(process, cleanup, overflow.Task);
                LogCleanupUnconfirmed(logger, processId, exit.IsCompleted, stdout.IsCompleted,
                    stderr.IsCompleted, termination.IsCompleted);
                throw new TimeoutException("Command process cleanup is unconfirmed.");
            }
            catch (Exception error)
            {
                LogProcessFailure(logger, error.GetType().Name);
                failure ??= ExceptionDispatchInfo.Capture(error);
            }
            finally
            {
                _ = overflow.Task.Exception;
            }
            failure?.Throw();
            ct.ThrowIfCancellationRequested();
            return overflow.Task.IsCompleted
                ? new ProcessResult(process.ExitCode, string.Empty, string.Empty, ProcessFailure.OutputLimitExceeded)
                : new ProcessResult(process.ExitCode, await stdout, await stderr, ProcessFailure.None);
        }
        finally
        {
            if (!retainedForCleanup)
            {
                process?.Dispose();
                _slots.Writer.TryWrite(0);
            }
        }
    }

    private void RetainUntilCompleted(Process process, Task completion, Task stop)
    {
        // A descendant can retain a pipe after the root exits. Keep its slot and
        // handles owned until both readers finish; never dispose under a live read.
        _unfinished.TryAdd(process, completion);
        _ = completion.ContinueWith(completed =>
        {
            _ = stop.Exception;
            if (completed.Exception is { } error)
                LogProcessFailure(logger, error.GetType().Name);
            process.Dispose();
            _unfinished.TryRemove(process, out _);
            _slots.Writer.TryWrite(0);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static void KillIfRunning(Process process)
    {
        if (process.HasExited)
            return;
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            // The root exited between inspection and termination.
        }
    }

    private static Channel<byte> CreateSlots()
    {
        var slots = Channel.CreateBounded<byte>(MaxConcurrentProcesses);
        for (var index = 0; index < MaxConcurrentProcesses; index++)
            slots.Writer.TryWrite(0);
        return slots;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Command process {ProcessId} cleanup deadline expired (exit: {ExitCompleted}, stdout: {StdoutCompleted}, stderr: {StderrCompleted}, termination: {TerminationCompleted}); execution slot retained until cleanup finishes")]
    private static partial void LogCleanupUnconfirmed(ILogger logger, int processId, bool exitCompleted,
        bool stdoutCompleted, bool stderrCompleted, bool terminationCompleted);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Command process failed ({ErrorType})")]
    private static partial void LogProcessFailure(ILogger logger, string errorType);
}
