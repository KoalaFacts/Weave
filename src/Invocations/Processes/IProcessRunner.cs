namespace Weave.Invocations.Processes;

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct);
}

public enum ProcessFailure
{
    None,
    OutputLimitExceeded,
    CapacityExhausted
}

public sealed record ProcessResult(int? ExitCode, string StandardOutput, string StandardError, ProcessFailure Failure);
