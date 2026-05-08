using Weave.Actions.Context;

namespace Weave.Cli.Shell;

internal sealed class WorkspaceDownCliCommand(IWorkspaceDownDependencies dependencies, WorkspacePrompt workspacePrompt) : ICliCommand<WorkspaceDownOptions>
{
    public string Name => "down";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Stop a workspace";

    public async Task<int> ExecuteAsync(WorkspaceDownOptions options, CancellationToken ct)
    {
        var name = workspacePrompt.SelectName(options.Name, "Which workspace would you like to stop?");
        var manifestPath = options.ManifestPath ?? dependencies.ResolveManifestPath(name);
        if (manifestPath is null)
        {
            WorkspacePrompt.WriteManifestNotFound(name);
            return 1;
        }

        var statePath = dependencies.GetWorkspaceStatePath(manifestPath);
        if (options.WorkspaceId is null && !dependencies.FileExists(statePath))
        {
            CliTheme.WriteError("Workspace is not running locally. No workspace id was found.");
            return 1;
        }

        string workspaceId;
        if (options.WorkspaceId is not null)
        {
            workspaceId = options.WorkspaceId;
            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                CliTheme.WriteError("--workspace-id is empty.");
                return 1;
            }
        }
        else
        {
            workspaceId = (await dependencies.ReadAllTextAsync(statePath, ct)).Trim();
            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                CliTheme.WriteError($"Workspace state file '{statePath}' is empty.");
                return 1;
            }
        }

        var result = await dependencies.StopWorkspaceAsync(workspaceId, ct);
        if (!result.IsSuccess)
        {
            if (result.Failure.Reason == ActionFailureReason.Cancelled)
                return 130;

            CliTheme.WriteError($"Failed to stop workspace: {result.Failure.Message}");
            return 1;
        }

        CliTheme.WriteSuccess($"Workspace '{workspaceId}' stopped.");

        try
        {
            if (dependencies.FileExists(statePath))
                dependencies.DeleteFile(statePath);

            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CliTheme.WriteWarning($"Could not remove local state file '{statePath}': {ex.Message}");
            return 0;
        }
    }
}
