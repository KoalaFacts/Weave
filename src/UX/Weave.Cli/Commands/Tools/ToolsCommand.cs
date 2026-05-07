using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class ToolsCommand
{
    public static Command Create(ToolsCliCommand handler)
    {
        var nameArg = new Argument<string?>("workspace")
        {
            Description = "Workspace name (interactive selection when omitted)",
            Arity = ArgumentArity.ZeroOrOne
        };

        var cmd = new Command("tools", "List tools in a workspace") { nameArg };
        cmd.SetAction((parseResult, ct) =>
            handler.ExecuteAsync(new WorkspaceNameOptions(parseResult.GetValue(nameArg)), ct));
        return cmd;
    }
}
