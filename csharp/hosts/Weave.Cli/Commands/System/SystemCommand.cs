using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class SystemCommand
{
    public static Command Create(SystemCliCommand handler)
    {
        var cmd = new Command("system", "Show silo and CLI config info");
        cmd.SetAction((_, ct) => handler.ExecuteAsync(new NoCliOptions(), ct));
        return cmd;
    }
}
