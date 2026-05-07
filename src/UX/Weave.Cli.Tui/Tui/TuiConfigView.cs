using Weave.Actions.Config;


namespace Weave.Cli.Tui;

internal sealed class TuiConfigView
{
    private readonly GetConfigAction _action;

    public TuiConfigView(GetConfigAction action)
    {
        _action = action;
    }

    public async Task ShowAsync(CancellationToken cancellationToken)
    {
        CliTheme.WriteSection("CLI Configuration");

        var result = await _action.ExecuteAsync(new GetConfigInput(), cancellationToken);
        if (!result.IsSuccess)
        {
            CliTheme.WriteError(result.Failure.Message);
            return;
        }

        ConfigSummaryRenderer.Render(result.Value.Summary);
        CliTheme.WriteMuted("Change settings with: weave config set <key> <value>");
    }
}
