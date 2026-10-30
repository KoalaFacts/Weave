using System.Diagnostics;
using System.Runtime.InteropServices;
using Weave.Actions.Dashboard;

namespace Weave.Cli.Shell;

internal sealed class WebUiCliCommand : ICliCommand<WebUiOptions>
{
    private readonly GetDashboardStatusAction _action;

    public WebUiCliCommand(GetDashboardStatusAction action)
    {
        _action = action;
    }

    public string Name => "webui";

    public IReadOnlyList<string> Aliases => ["web", "w"];

    public string Description => "Open the Weave web dashboard";

    public async Task<int> ExecuteAsync(WebUiOptions options, CancellationToken ct)
    {
        var requestedUrl = options.Url ?? Environment.GetEnvironmentVariable("WEAVE_WEBUI_URL");
        var result = await _action.ExecuteAsync(new GetDashboardStatusInput(requestedUrl), ct);
        if (!result.IsSuccess)
        {
            CliTheme.WriteError(result.Failure.Message);
            return 1;
        }

        CliTheme.WriteKeyValue("Web UI", result.Value.Url);
        if (result.Value.Reachable)
            CliTheme.WriteSuccess("Dashboard is reachable.");
        else
            CliTheme.WriteWarning("Dashboard is not reachable yet. Start it with `weave run` or the AppHost.");

        if (options.NoOpen)
            return 0;

        if (TryOpenBrowser(result.Value.Url))
            CliTheme.WriteMuted("Opened in your default browser.");
        else
            CliTheme.WriteMuted($"Could not open a browser automatically. Visit: {result.Value.Url}");

        return 0;
    }

    private static bool TryOpenBrowser(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
                return true;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", url);
                return true;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }

        return false;
    }
}
