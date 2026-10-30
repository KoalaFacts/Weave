using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Weave.Invocations.Processes;

public interface IProcessRuntimeObserver
{
    ProcessRuntimeSnapshot Observe();
}

public sealed record ProcessRuntimeSnapshot(DateTimeOffset ObservedAt, int Capacity, int Available,
    ImmutableArray<ProcessExecutionSnapshot> Executions);

public sealed record ProcessExecutionSnapshot(Guid ExecutionId, DateTimeOffset AdmittedAt, TimeSpan Elapsed,
    ProcessExecutionPhase Phase, ProcessTaskState Exit, ProcessTaskState StandardOutput,
    ProcessTaskState StandardError, ProcessTaskState Termination);

[JsonConverter(typeof(JsonStringEnumConverter<ProcessExecutionPhase>))]
public enum ProcessExecutionPhase
{
    Starting,
    Running,
    CleaningUp,
    CleanupUnconfirmed
}

[JsonConverter(typeof(JsonStringEnumConverter<ProcessTaskState>))]
public enum ProcessTaskState
{
    NotStarted,
    Pending,
    Completed,
    Faulted
}
