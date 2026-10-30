using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class AuditReplayCommand
{
    public static Command Create(AuditReplayCliCommand handler)
    {
        var tokenArg = new Argument<string?>("tokenId")
        {
            Description = "Capability token id to replay. Omit to pick from recent tokens.",
            Arity = ArgumentArity.ZeroOrOne
        };
        var limitOption = new Option<int>("--limit")
        {
            Description = "When tokenId is omitted, the number of recent rows to scan when offering choices.",
            DefaultValueFactory = _ => 100
        };

        var cmd = new Command("replay", "Replay every authorization decision (allow + deny) for a capability token.")
        {
            tokenArg,
            limitOption
        };

        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var tokenId = parseResult.GetValue(tokenArg);
            var limit = parseResult.GetValue(limitOption);
            return await handler.ExecuteAsync(new AuditReplayOptions(tokenId, limit), cancellationToken);
        });

        return cmd;
    }
}

internal static class AuditCommands
{
    public static Command Create(AuditReplayCliCommand replayHandler)
    {
        var cmd = new Command("audit", "Inspect capability authorization rows recorded by the silo");
        cmd.Subcommands.Add(AuditReplayCommand.Create(replayHandler));
        return cmd;
    }
}
