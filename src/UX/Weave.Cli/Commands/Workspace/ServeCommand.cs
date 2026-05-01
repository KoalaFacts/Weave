using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class WorkspaceServeCommand
{
    public static Command Create()
    {
        var portOption = new Option<int>("--port")
        {
            Description = "Port to listen on",
            DefaultValueFactory = _ => CliConfigStore.Load().DefaultPort
        };
        var backgroundOption = new Option<bool>("--background") { Description = "Run in the background" };

        var cmd = new Command("serve", "Start the local Weave server") { portOption, backgroundOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var port = parseResult.GetValue(portOption);
            var background = parseResult.GetValue(backgroundOption);
            return await new ServeCliCommand().ExecuteAsync(new ServeOptions(port, background), cancellationToken);
        });

        return cmd;
    }
}
