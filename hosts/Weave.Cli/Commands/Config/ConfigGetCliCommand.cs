using Spectre.Console;
using Weave.Actions.Config;

namespace Weave.Cli.Commands;

/// <summary>
/// Phase 1 read-only verb on the CLI side. Delegates the data load to
/// <see cref="GetConfigAction"/>; the command file holds only the Spectre
/// rendering. Single-key path prints just the value; whole-snapshot path
/// prints the curated table.
/// </summary>
internal sealed class ConfigGetCliCommand : ICliCommand<ConfigGetOptions>
{
    private readonly GetConfigAction _action;

    public ConfigGetCliCommand(GetConfigAction action)
    {
        _action = action;
    }

    public string Name => "get";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Show configuration values";

    public async Task<int> ExecuteAsync(ConfigGetOptions options, CancellationToken ct)
    {
        var result = await _action.ExecuteAsync(new GetConfigInput(options.Key), ct);
        if (!result.IsSuccess)
        {
            CliTheme.WriteError(result.Failure.Message);
            return 1;
        }

        if (result.Value.RequestedValue is { } value)
        {
            AnsiConsole.WriteLine(value);
            return 0;
        }

        ConfigSummaryRenderer.Render(result.Value.Summary);
        return 0;
    }
}
