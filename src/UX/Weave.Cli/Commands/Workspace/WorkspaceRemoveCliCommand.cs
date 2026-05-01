namespace Weave.Cli.Commands;

internal sealed class WorkspaceRemoveCliCommand : ICliCommand<WorkspaceRemoveOptions>
{
    public string Name => "remove";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Remove a workspace";

    public Task<int> ExecuteAsync(WorkspaceRemoveOptions options, CancellationToken ct)
    {
        var name = WorkspacePrompt.SelectRegisteredName(options.Name, "Which workspace would you like to remove?");
        if (string.IsNullOrWhiteSpace(name))
        {
            CliTheme.WriteError("No workspaces found. Create one first with: weave workspace new");
            return Task.FromResult(1);
        }

        var path = WorkspaceRegistry.Resolve(name);
        if (path is null)
        {
            CliTheme.WriteError($"Workspace '{name}' not found in registry.");
            return Task.FromResult(1);
        }

        WorkspaceRegistry.Unregister(name);

        if (options.Purge && Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
            CliTheme.WriteSuccess($"Workspace '{name}' purged.");
        }
        else
        {
            CliTheme.WriteInfo($"Workspace '{name}' deregistered.");
            CliTheme.WriteMuted("  Use --purge to delete files.");
        }

        return Task.FromResult(0);
    }
}
