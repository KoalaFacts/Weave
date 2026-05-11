using Weave.Actions.Config;
using Weave.Cli.Tui.Verbs;

namespace Weave.Cli.Tui;

internal sealed class TuiConfigView(GetConfigAction action) : ITuiVerb
{
    public string Name => "config";

    public IReadOnlyList<string> Aliases => [];

    public async Task DispatchAsync(TuiVerbContext context, CancellationToken ct)
    {
        CliTheme.WriteSection("CLI Configuration");

        var result = await action.ExecuteAsync(new GetConfigInput(), ct);
        if (!result.IsSuccess)
        {
            CliTheme.WriteError(result.Failure.Message);
            return;
        }

        ConfigSummaryRenderer.Render(result.Value.Summary);
        CliTheme.WriteMuted("Change settings with: weave config set <key> <value>");
    }
}
