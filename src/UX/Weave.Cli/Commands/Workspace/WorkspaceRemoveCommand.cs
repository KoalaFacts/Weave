using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class WorkspaceRemoveCommand
{
    public static Command Create()
    {
        var nameArg = new Argument<string>("name") { Description = "Workspace name" };
        nameArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);
        var purgeOption = new Option<bool>("--purge") { Description = "Delete workspace folder" };

        var cmd = new Command("remove", "Remove a workspace") { nameArg, purgeOption };
        cmd.SetAction(parseResult =>
        {
            var name = parseResult.GetValue(nameArg)!;
            var purge = parseResult.GetValue(purgeOption);

            var path = WorkspaceRegistry.Resolve(name);
            if (path is null)
            {
                CliTheme.WriteError($"Workspace '{name}' not found in registry.");
                return 1;
            }

            WorkspaceRegistry.Unregister(name);

            if (purge && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                CliTheme.WriteSuccess($"Workspace '{name}' purged.");
            }
            else
            {
                CliTheme.WriteInfo($"Workspace '{name}' deregistered.");
                CliTheme.WriteMuted("  Use --purge to delete files.");
            }

            return 0;
        });

        return cmd;
    }
}
