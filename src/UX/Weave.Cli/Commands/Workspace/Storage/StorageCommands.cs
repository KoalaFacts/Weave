using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class StorageCommands
{
    public static Command Create()
    {
        var cmd = new Command("storage", "View and change the storage backend");

        cmd.Subcommands.Add(CreateShowCommand());
        cmd.Subcommands.Add(CreateChangeCommand());

        return cmd;
    }

    private static Command CreateShowCommand()
    {
        var cmd = new Command("show", "Show the current storage configuration");
        cmd.SetAction((_, cancellationToken) => new StorageShowCliCommand().ExecuteAsync(new NoCliOptions(), cancellationToken));

        return cmd;
    }

    private static Command CreateChangeCommand()
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
            return await new StorageChangeCliCommand().ExecuteAsync(new StorageChangeOptions(backend, connectionStr, migrate), cancellationToken);
        });

        return cmd;
    }
}
