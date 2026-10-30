namespace Weave.Invocations.Processes;

public sealed partial class ProcessRunner
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Execution> _executions = [];

    public ProcessRuntimeSnapshot Observe()
    {
        lock (_gate)
        {
            var observedAt = clock.GetUtcNow();
            var timestamp = clock.GetTimestamp();
            return new ProcessRuntimeSnapshot(observedAt, MaxConcurrentProcesses, MaxConcurrentProcesses - _executions.Count,
                [.. _executions.Values.Select(execution => new ProcessExecutionSnapshot(execution.Id, execution.AdmittedAt,
                    clock.GetElapsedTime(execution.Timestamp, timestamp), execution.Phase,
                    State(execution.Exit), State(execution.StandardOutput), State(execution.StandardError), State(execution.Termination)))]);
        }
    }

    private Execution? TryAdmit()
    {
        lock (_gate)
        {
            if (_executions.Count == MaxConcurrentProcesses)
                return null;
            var execution = new Execution(Guid.NewGuid(), clock.GetUtcNow(), clock.GetTimestamp());
            _executions.Add(execution.Id, execution);
            return execution;
        }
    }

    private void MarkRunning(Execution execution, Task exit, Task stdout, Task stderr)
    {
        lock (_gate)
        {
            execution.Exit = exit;
            execution.StandardOutput = stdout;
            execution.StandardError = stderr;
            execution.Phase = ProcessExecutionPhase.Running;
        }
    }

    private void MarkCleanup(Execution execution, Task termination)
    {
        lock (_gate)
        {
            execution.Termination = termination;
            execution.Phase = ProcessExecutionPhase.CleaningUp;
        }
    }

    private void MarkUnconfirmed(Execution execution)
    {
        lock (_gate)
            execution.Phase = ProcessExecutionPhase.CleanupUnconfirmed;
    }

    private void Release(Execution execution)
    {
        lock (_gate)
            _executions.Remove(execution.Id);
    }

    private static ProcessTaskState State(Task? task) => task switch
    {
        null => ProcessTaskState.NotStarted,
        { IsFaulted: true } => ProcessTaskState.Faulted,
        { IsCompletedSuccessfully: true } => ProcessTaskState.Completed,
        _ => ProcessTaskState.Pending
    };

    private sealed class Execution(Guid id, DateTimeOffset admittedAt, long timestamp)
    {
        public Guid Id { get; } = id;
        public DateTimeOffset AdmittedAt { get; } = admittedAt;
        public long Timestamp { get; } = timestamp;
        public ProcessExecutionPhase Phase { get; set; } = ProcessExecutionPhase.Starting;
        public Task? Exit { get; set; }
        public Task? StandardOutput { get; set; }
        public Task? StandardError { get; set; }
        public Task? Termination { get; set; }
    }
}
