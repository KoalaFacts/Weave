using System.Diagnostics;
using System.Runtime.InteropServices;
using Weave.Shared;

namespace Weave.Cli.Commands;

internal sealed class WebUiCliCommand : ICliCommand<WebUiOptions>
{
    public string Name => "webui";

    public IReadOnlyList<string> Aliases => ["web", "w"];

    public string Description => "Open the Weave web dashboard";

    public async Task<int> ExecuteAsync(WebUiOptions options, CancellationToken ct)
    {
        var url = options.Url ?? DefaultUrl();
        CliTheme.WriteKeyValue("Web UI", url);

        var reachable = await IsReachableAsync(url, ct);
        if (reachable)
            CliTheme.WriteSuccess("Dashboard is reachable.");
        else
            CliTheme.WriteWarning("Dashboard is not reachable yet. Start it with `weave run` or the AppHost.");

        if (options.NoOpen)
            return 0;

        if (TryOpenBrowser(url))
            CliTheme.WriteMuted("Opened in your default browser.");
        else
            CliTheme.WriteMuted($"Could not open a browser automatically. Visit: {url}");

        return 0;
    }

    private static string DefaultUrl()
    {
        var envUrl = Environment.GetEnvironmentVariable("WEAVE_WEBUI_URL");
        if (!string.IsNullOrWhiteSpace(envUrl))
            return envUrl;

        var config = CliConfigStore.Load();
        var port = config.DefaultPort == WeavePorts.SiloHttp
            ? WeavePorts.DashboardHttp
            : config.DefaultPort + 1;

        return $"http://localhost:{port}";
    }

    private static async Task<bool> IsReachableAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.IsSuccessStatusCode || (int)response.StatusCode < 500;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException)
        {
            return false;
        }
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
