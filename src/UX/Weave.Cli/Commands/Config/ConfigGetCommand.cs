using System.CommandLine;
using System.Globalization;

namespace Weave.Cli.Commands;

internal static class ConfigGetCommand
{
    public static Command Create()
    {
        var keyArg = new Argument<string?>("key")
        {
            Description = "Config key to read (omit to show all)",
            Arity = ArgumentArity.ZeroOrOne
        };
        keyArg.CompletionSources.Add(CliCompletions.CompleteConfigKeys);

        var cmd = new Command("get", "Show configuration values") { keyArg };
        cmd.SetAction((parseResult, cancellationToken) =>
        {
            var key = parseResult.GetValue(keyArg);
            return new ConfigGetCliCommand().ExecuteAsync(new ConfigGetOptions(key), cancellationToken);
        });

        return cmd;
    }

    internal static string? GetValue(CliConfig config, string key) => key.ToLowerInvariant() switch
    {
        "version" => config.Version,
        "silopath" => config.SiloPath ?? "(not set)",
        "defaultport" => config.DefaultPort.ToString(CultureInfo.InvariantCulture),
        _ => null
    };
}
