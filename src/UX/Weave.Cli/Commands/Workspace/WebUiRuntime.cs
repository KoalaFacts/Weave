using System.Diagnostics;
using System.Runtime.InteropServices;
using Weave.Shared;

namespace Weave.Cli.Commands;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is injected for CLI/TUI testability.")]
internal sealed class WebUiRuntime
{
    public string DefaultUrl()
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

    public async Task<bool> IsReachableAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.IsSuccessStatusCode || (int)response.StatusCode < 500;
        }
        catch
        {
            return false;
        }
    }

    public bool TryOpenBrowser(string url)
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
        catch
        {
            return false;
        }

        return false;
    }
}
