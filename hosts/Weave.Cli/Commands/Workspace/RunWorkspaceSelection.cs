namespace Weave.Cli.Commands;

internal sealed record RunWorkspaceSelection(bool ShouldRun, int ExitCode, string? Name, string? ManifestPath)
{
    public static RunWorkspaceSelection Run(string? name, string manifestPath) => new(true, 0, name, manifestPath);

    public static RunWorkspaceSelection Stop(int exitCode) => new(false, exitCode, null, null);
}
