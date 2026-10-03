namespace Weave.Cli.Commands.Local;

internal interface ILocalCodexLauncher
{
    Task<int> RunAsync(string directory, LocalDeployment deployment, string executable,
        string agentDirectory, string? task, bool execute, CancellationToken ct);
}
