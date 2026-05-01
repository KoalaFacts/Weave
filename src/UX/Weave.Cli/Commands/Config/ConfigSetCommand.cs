using System.CommandLine;

namespace Weave.Cli.Commands;

internal static class ConfigSetCommand
{
    public static Command Create()
    {
        var keyArg = new Argument<string>("key") { Description = "Config key" };
        keyArg.CompletionSources.Add(CliCompletions.CompleteConfigKeys);
        var valueArg = new Argument<string>("value") { Description = "Config value" };

        var cmd = new Command("set", "Update a configuration value") { keyArg, valueArg };
        cmd.SetAction((parseResult, cancellationToken) =>
        {
            var key = parseResult.GetValue(keyArg)!;
            var value = parseResult.GetValue(valueArg)!;
            return new ConfigSetCliCommand().ExecuteAsync(new ConfigSetOptions(key, value), cancellationToken);
        });

        return cmd;
    }
}
