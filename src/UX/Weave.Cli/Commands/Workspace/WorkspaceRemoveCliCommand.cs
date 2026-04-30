namespace Weave.Cli.Commands;

internal sealed class WorkspaceRemoveCliCommand : ICliCommand<WorkspaceRemoveOptions>
{
    public string Name => "remove";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Remove a workspace";

    public Task<int> ExecuteAsync(WorkspaceRemoveOptions options, CancellationToken ct)
    {
        var path = WorkspaceRegistry.Resolve(options.Name);
        if (path is null)
        {
            CliTheme.WriteError($"Workspace '{options.Name}' not found in registry.");
            return Task.FromResult(1);
        }

        WorkspaceRegistry.Unregister(options.Name);

        if (options.Purge && Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
            CliTheme.WriteSuccess($"Workspace '{options.Name}' purged.");
        }
        else
        {
            CliTheme.WriteInfo($"Workspace '{options.Name}' deregistered.");
            CliTheme.WriteMuted("  Use --purge to delete files.");
        }

        return Task.FromResult(0);
    }
}
