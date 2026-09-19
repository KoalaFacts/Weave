using Weave.Actions.SystemInfo;
using Weave.Cli.Tui.Verbs;

namespace Weave.Cli.Tui;

internal sealed class TuiSystemView(GetSystemInfoAction action) : ITuiVerb
{
    public string Name => "system";

    public IReadOnlyList<string> Aliases => ["sys"];

    public async Task DispatchAsync(TuiVerbContext context, CancellationToken ct)
    {
        var result = await action.ExecuteAsync(new GetSystemInfoInput(), ct);
        if (!result.IsSuccess)
        {
            CliTheme.WriteError(result.Failure.Message);
            return;
        }

        SystemInfoRenderer.Render(result.Value);
    }
}
