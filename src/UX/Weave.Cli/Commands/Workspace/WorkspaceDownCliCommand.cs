namespace Weave.Cli.Commands;

internal sealed class WorkspaceDownCliCommand(IWorkspaceDownDependencies dependencies) : ICliCommand<WorkspaceDownOptions>
{
    public WorkspaceDownCliCommand()
        : this(new DefaultWorkspaceDownDependencies())
    {
    }

    public string Name => "down";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Stop a workspace";

    public async Task<int> ExecuteAsync(WorkspaceDownOptions options, CancellationToken ct)
    {
        var manifestPath = options.ManifestPath ?? dependencies.ResolveManifestPath(options.Name);
        if (manifestPath is null)
        {
            CliTheme.WriteError($"No workspace.json found for '{options.Name}'.");
            return 1;
        }

        var statePath = dependencies.GetWorkspaceStatePath(manifestPath);
        if (options.WorkspaceId is null && !dependencies.FileExists(statePath))
        {
            CliTheme.WriteError("Workspace is not running locally. No workspace id was found.");
            return 1;
        }

        var workspaceId = options.WorkspaceId ?? (await dependencies.ReadAllTextAsync(statePath, ct)).Trim();
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            CliTheme.WriteError("Workspace state file is empty.");
            return 1;
        }

        try
        {
            await dependencies.StopWorkspaceAsync(workspaceId, ct);
            if (dependencies.FileExists(statePath))
                dependencies.DeleteFile(statePath);

            CliTheme.WriteSuccess($"Workspace '{workspaceId}' stopped.");
            return 0;
        }
        catch (Exception ex)
        {
            CliTheme.WriteError($"Failed to stop workspace: {ex.Message}");
            return 1;
        }
    }
}
