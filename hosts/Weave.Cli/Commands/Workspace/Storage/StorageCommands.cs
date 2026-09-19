using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class StorageCommands
{
    public static Command Create(StorageShowCliCommand showHandler, StorageChangeCliCommand changeHandler)
    {
        var cmd = new Command("storage", "View and change the storage backend");

        cmd.Subcommands.Add(CreateShowCommand(showHandler));
        cmd.Subcommands.Add(CreateChangeCommand(changeHandler));

        return cmd;
    }

    private static Command CreateShowCommand(StorageShowCliCommand handler)
    {
        var cmd = new Command("show", "Show the current storage configuration");
        cmd.SetAction((_, cancellationToken) => handler.ExecuteAsync(new NoCliOptions(), cancellationToken));

        return cmd;
    }

    private static Command CreateChangeCommand(StorageChangeCliCommand handler)
    {
        var backendArg = new Argument<string?>("backend")
        {
            Description = "Target backend (memory, sqlite, postgresql, sqlserver, redis)",
            Arity = ArgumentArity.ZeroOrOne
        };
        var connectionOption = new Option<string?>("--connection") { Description = "Connection string for the new backend" };
        var migrateOption = new Option<bool>("--migrate") { Description = "Export data before switching and import after" };

        var cmd = new Command("change", "Switch to a different storage backend") { backendArg, connectionOption, migrateOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var backend = parseResult.GetValue(backendArg);
            var connectionStr = parseResult.GetValue(connectionOption);
            var migrate = parseResult.GetValue(migrateOption);
            return await handler.ExecuteAsync(new StorageChangeOptions(backend, connectionStr, migrate), cancellationToken);
        });

        return cmd;
    }
}
