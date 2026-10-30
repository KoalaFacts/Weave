using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class TasksCommand
{
    public static Command Create(TasksCliCommand handler)
    {
        var workspaceArg = new Argument<string?>("workspace")
        {
            Description = "Workspace name (interactive selection when omitted)",
            Arity = ArgumentArity.ZeroOrOne
        };

        var agentArg = new Argument<string?>("agent")
        {
            Description = "Agent name (interactive selection when omitted)",
            Arity = ArgumentArity.ZeroOrOne
        };

        var cmd = new Command("tasks", "List tasks for an agent in a workspace") { workspaceArg, agentArg };
        cmd.SetAction((parseResult, ct) =>
            handler.ExecuteAsync(
                new TasksOptions(parseResult.GetValue(workspaceArg), parseResult.GetValue(agentArg)),
                ct));
        return cmd;
    }
}
