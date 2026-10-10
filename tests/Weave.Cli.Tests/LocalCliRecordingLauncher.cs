using Weave.Cli.Commands.Local;

namespace Weave.Cli.Tests;

internal sealed class LocalCliRecordingLauncher : ILocalCodexLauncher
{
    public List<(string Directory, LocalDeployment Deployment, string Executable, string AgentDirectory,
        string? Task, bool Execute, CancellationToken Cancellation)> Calls
    { get; } = [];
    public Task<int> RunAsync(string directory, LocalDeployment deployment, string executable,
        string agentDirectory, string? task, bool execute, CancellationToken ct)
    {
        Calls.Add((directory, deployment, executable, agentDirectory, task, execute, ct));
        return System.Threading.Tasks.Task.FromResult(27);
    }
}
