using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class WebUiCommand
{
    public static Command Create()
    {
        var urlOption = new Option<string?>("--url")
        {
            Description = "Web UI URL (defaults to http://localhost:9403)"
        };
        var noOpenOption = new Option<bool>("--no-open")
        {
            Description = "Print the URL and health status but don't open a browser"
        };

        var cmd = new Command("webui", "Open the Weave web dashboard") { urlOption, noOpenOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var url = parseResult.GetValue(urlOption);
            var noOpen = parseResult.GetValue(noOpenOption);
            return await new WebUiCliCommand().ExecuteAsync(new WebUiOptions(url, noOpen), cancellationToken);
        });

        return cmd;
    }
}
