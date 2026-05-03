using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class RunCommand
{
    public static Command Create()
    {
        var nameArg = new Argument<string?>("name")
        {
            Description = "Workspace name (defaults to current directory)",
            DefaultValueFactory = _ => null,
            Arity = ArgumentArity.ZeroOrOne
        };
        var portOption = new Option<int>("--port")
        {
            Description = "Server port",
            DefaultValueFactory = _ => CliConfigStore.Load().DefaultPort
        };

        var cmd = new Command("run", "Start the server and workspace in one command") { nameArg, portOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArg);
            var port = parseResult.GetValue(portOption);
            return await new RunCliCommand().ExecuteAsync(new RunOptions(name, port), cancellationToken);
        });

        return cmd;
    }
}
