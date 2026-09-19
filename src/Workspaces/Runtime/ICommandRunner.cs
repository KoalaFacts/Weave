namespace Weave.Workspaces.Runtime;

public interface ICommandRunner
{
    Task<string> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken ct);
}
