using System.CommandLine;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Weave.Shared;

namespace Weave.Cli.Commands;

internal static class WebUiCommand
{
    public static Command Create()
    {
        var urlOption = new Option<string?>("--url")
        {
            Description = "Web UI URL (defaults to http://localhost:9403)"
        };
        var noOpenOption = new Option<bool>("--no-open")
        {
            Description = "Print the URL and health status but don't open a browser"
        };

        var cmd = new Command("webui", "Open the Weave web dashboard") { urlOption, noOpenOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var url = parseResult.GetValue(urlOption) ?? DefaultUrl();
            var noOpen = parseResult.GetValue(noOpenOption);
            return await new WebUiCliCommand().ExecuteAsync(new WebUiOptions(url, noOpen), cancellationToken);
        });

        return cmd;
    }

    internal static string DefaultUrl()
    {
        var envUrl = Environment.GetEnvironmentVariable("WEAVE_WEBUI_URL");
        if (!string.IsNullOrWhiteSpace(envUrl))
            return envUrl;

        // Convention from RunCommand: dashboard lives at silo port + 1.
        var config = CliConfigStore.Load();
        var port = config.DefaultPort == WeavePorts.SiloHttp
            ? WeavePorts.DashboardHttp
            : config.DefaultPort + 1;

        return $"http://localhost:{port}";
    }

    internal static async Task<bool> IsReachableAsync(string url, CancellationToken cancellationToken)
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

    internal static bool TryOpenBrowser(string url)
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
