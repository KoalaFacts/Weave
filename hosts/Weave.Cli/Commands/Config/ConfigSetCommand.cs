using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class ConfigSetCommand
{
    public static Command Create(ConfigSetCliCommand handler)
    {
        var keyArg = new Argument<string?>("key")
        {
            Description = "Config key",
            Arity = ArgumentArity.ZeroOrOne
        };
        keyArg.CompletionSources.Add(CliCompletions.CompleteConfigKeys);
        var valueArg = new Argument<string?>("value")
        {
            Description = "Config value",
            Arity = ArgumentArity.ZeroOrOne
        };

        var cmd = new Command("set", "Update a configuration value") { keyArg, valueArg };
        cmd.SetAction((parseResult, cancellationToken) =>
        {
            var key = parseResult.GetValue(keyArg);
            var value = parseResult.GetValue(valueArg);
            return handler.ExecuteAsync(new ConfigSetOptions(key, value), cancellationToken);
        });

        return cmd;
    }
}
