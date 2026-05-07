using Weave.Actions.SystemInfo;


namespace Weave.Cli.Tui;

internal sealed class TuiSystemView
{
    private readonly GetSystemInfoAction _action;

    public TuiSystemView(GetSystemInfoAction action)
    {
        _action = action;
    }

    public async Task ShowAsync(CancellationToken cancellationToken)
    {
        var result = await _action.ExecuteAsync(new GetSystemInfoInput(), cancellationToken);
        if (!result.IsSuccess)
        {
            CliTheme.WriteError(result.Failure.Message);
            return;
        }

        SystemInfoRenderer.Render(result.Value);
    }
}
