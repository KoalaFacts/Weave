using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class WorkspaceStorageCommands
{
    public static Command Create()
    {
        var cmd = new Command("storage", "View and change workspace-level storage");

        cmd.Subcommands.Add(CreateShowCommand());
        cmd.Subcommands.Add(CreateChangeCommand());

        return cmd;
    }

    private static Command CreateShowCommand()
    {
        var workspaceArg = new Argument<string?>("workspace")
        {
            Description = "Workspace name",
            Arity = ArgumentArity.ZeroOrOne
        };
        workspaceArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);

        var cmd = new Command("show", "Show workspace storage configuration") { workspaceArg };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var workspace = parseResult.GetValue(workspaceArg);
            return await new WorkspaceStorageShowCliCommand().ExecuteAsync(new WorkspaceNameOptions(workspace), cancellationToken);
        });

        return cmd;
    }

    private static Command CreateChangeCommand()
    {
        var workspaceArg = new Argument<string?>("workspace")
        {
            Description = "Workspace name",
            Arity = ArgumentArity.ZeroOrOne
        };
        workspaceArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);
        var backendArg = new Argument<string?>("backend")
        {
            Description = "Target backend (memory, sqlite, postgresql, sqlserver, redis)",
            Arity = ArgumentArity.ZeroOrOne
        };
        var connectionOption = new Option<string?>("--connection") { Description = "Connection string" };
        var schemaOption = new Option<string?>("--schema") { Description = "Database schema for isolation (e.g. workspace name)" };
        var databaseOption = new Option<string?>("--database") { Description = "Database name for database isolation mode" };
        var isolationOption = new Option<string?>("--isolation") { Description = "Isolation mode: schema (share db, separate schema) or database (separate db)" };

        var cmd = new Command("change", "Change workspace storage backend") { workspaceArg, backendArg, connectionOption, schemaOption, databaseOption, isolationOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var workspace = parseResult.GetValue(workspaceArg);
            var backend = parseResult.GetValue(backendArg);
            var connectionStr = parseResult.GetValue(connectionOption);
            var schema = parseResult.GetValue(schemaOption);
            var database = parseResult.GetValue(databaseOption);
            var isolationStr = parseResult.GetValue(isolationOption);
            return await new WorkspaceStorageChangeCliCommand().ExecuteAsync(
                new WorkspaceStorageChangeOptions(workspace, backend, connectionStr, schema, database, isolationStr),
                cancellationToken);
        });

        return cmd;
    }
}
