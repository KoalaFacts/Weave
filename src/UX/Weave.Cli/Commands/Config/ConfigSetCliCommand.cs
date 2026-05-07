using Weave.Actions.Config;
using Weave.Actions.Context;

namespace Weave.Cli.Commands;

internal sealed class ConfigSetCliCommand : ICliCommand<ConfigSetOptions>
{
    private readonly SetConfigAction _action;
    private readonly IActionPrompter _prompter;

    public ConfigSetCliCommand(SetConfigAction action, IActionPrompter prompter)
    {
        _action = action;
        _prompter = prompter;
    }

    public string Name => "set";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Update a configuration value";

    public async Task<int> ExecuteAsync(ConfigSetOptions options, CancellationToken ct)
    {
        var key = string.IsNullOrWhiteSpace(options.Key)
            ? await _prompter.PromptSelectionAsync("Which config value would you like to update?", ConfigKeys.Writable, ct)
            : options.Key;

        var value = string.IsNullOrWhiteSpace(options.Value)
            ? await _prompter.PromptTextAsync($"{key}:", cancellationToken: ct)
            : options.Value;

        var result = await _action.ExecuteAsync(new SetConfigInput(key, value), ct);
        if (!result.IsSuccess)
        {
            if (result.Failure.Reason == ActionFailureReason.Cancelled)
                return 130;

            CliTheme.WriteError(result.Failure.Message);
            return 1;
        }

        CliTheme.WriteSuccess($"{result.Value.Key} = {result.Value.Value}");
        return 0;
    }
}
