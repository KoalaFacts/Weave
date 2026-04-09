using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class WorkspaceDownCommand
{
    public static Command Create()
    {
        var nameArg = new Argument<string>("name") { Description = "Workspace name" };
        nameArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);

        var cmd = new Command("down", "Stop a workspace") { nameArg };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArg)!;

            var manifestPath = ManifestResolver.Resolve(name);
            if (manifestPath is null)
            {
                CliTheme.WriteError($"No workspace.json found for '{name}'.");
                return 1;
            }

            var statePath = WorkspaceApiClient.GetWorkspaceStatePath(manifestPath);
            if (!File.Exists(statePath))
            {
                CliTheme.WriteError("Workspace is not running locally. No workspace id was found.");
                return 1;
            }

            var workspaceId = (await File.ReadAllTextAsync(statePath, cancellationToken)).Trim();
            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                CliTheme.WriteError("Workspace state file is empty.");
                return 1;
            }

            try
            {
                using var client = new WorkspaceApiClient();
                await client.StopWorkspaceAsync(workspaceId, cancellationToken);
                File.Delete(statePath);
                CliTheme.WriteSuccess($"Workspace '{workspaceId}' stopped.");
            }
            catch (Exception ex)
            {
                CliTheme.WriteError($"Failed to stop workspace: {ex.Message}");
                return 1;
            }

            return 0;
        });

        return cmd;
    }
}
