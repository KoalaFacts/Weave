using Weave.Invocations.Processes;

namespace Weave.Workspaces.Runtime;

public sealed class ProcessCommandRunner(IProcessRunner processes) : ICommandRunner
{
    public async Task<string> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var result = await processes.RunAsync(command, arguments, ct);
        if (result.Failure == ProcessFailure.CapacityExhausted)
            throw new InvalidOperationException("Command process capacity is exhausted.");
        if (result.Failure == ProcessFailure.OutputLimitExceeded)
            throw new InvalidOperationException("Command output limit exceeded.");
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Command '{command}' failed (exit code {result.ExitCode}).");
        return result.StandardOutput;
    }
}
