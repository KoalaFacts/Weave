using System.CommandLine;
using System.Diagnostics;
using Spectre.Console;

namespace Weave.Cli.Commands;

internal static class WorkspaceServeCommand
{
    public static Command Create()
    {
        var portOption = new Option<int>("--port")
        {
            Description = "Port to listen on",
            DefaultValueFactory = _ => CliConfigStore.Load().DefaultPort
        };
        var backgroundOption = new Option<bool>("--background") { Description = "Run in the background" };

        var cmd = new Command("serve", "Start the local Weave server") { portOption, backgroundOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var port = parseResult.GetValue(portOption);
            var background = parseResult.GetValue(backgroundOption);

            if (await IsReachableAsync(port, cancellationToken))
            {
                CliTheme.WriteWarning($"Weave is already running on port {port}.");
                return 0;
            }

            var siloPath = ResolveSiloPath();
            if (siloPath is null)
            {
                CliTheme.WriteError("Could not locate the Weave silo.");
                CliTheme.WriteMuted("  Run `weave init` to configure, or set WEAVE_SILO_PATH.");
                return 1;
            }

            var args = BuildSiloArgs(siloPath, port);

            if (background)
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = args.FileName,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                foreach (var arg in args.Arguments)
                    startInfo.ArgumentList.Add(arg);

                var process = Process.Start(startInfo);
                if (process is null)
                {
                    CliTheme.WriteError("Failed to start silo process.");
                    return 1;
                }

                await WaitForReadyAsync(port, cancellationToken);

                CliTheme.WriteSuccess($"Weave running in background (PID {process.Id}, port {port}).");
                CliTheme.WriteMuted("  Local mode \u2014 no external services required.");
                CliTheme.WriteMuted($"  Stop with: weave serve stop or terminate PID {process.Id}.");
                return 0;
            }

            CliTheme.WriteBanner();
            CliTheme.WriteInfo($"Starting in local mode on port {port}...");
            CliTheme.WriteMuted("  No external services required. Press Ctrl+C to stop.");
            AnsiConsole.WriteLine();

            var fgStart = new ProcessStartInfo
            {
                FileName = args.FileName,
                UseShellExecute = false
            };

            foreach (var arg in args.Arguments)
                fgStart.ArgumentList.Add(arg);

            using var siloProcess = Process.Start(fgStart);
            if (siloProcess is null)
            {
                CliTheme.WriteError("Failed to start silo process.");
                return 1;
            }

            await siloProcess.WaitForExitAsync(cancellationToken);
            return siloProcess.ExitCode;
        });

        return cmd;
    }

    private static async Task<bool> IsReachableAsync(int port, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var response = await http.GetAsync($"http://localhost:{port}/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task WaitForReadyAsync(int port, CancellationToken ct)
    {
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(500, ct);
            if (await IsReachableAsync(port, ct))
                return;
        }
    }

    private static string? ResolveSiloPath()
    {
        // 1. Environment variable (highest priority)
        var envPath = Environment.GetEnvironmentVariable("WEAVE_SILO_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && (File.Exists(envPath) || Directory.Exists(envPath)))
            return envPath;

        // 2. CLI config file (~/.weave/config.json)
        var config = CliConfigStore.Load();
        if (!string.IsNullOrWhiteSpace(config.SiloPath) && (File.Exists(config.SiloPath) || Directory.Exists(config.SiloPath)))
            return config.SiloPath;

        // 3. Well-known dev paths relative to CWD
        var candidates = new[]
        {
            Path.Combine("src", "Runtime", "Weave.Silo"),
            Path.Combine("src", "Runtime", "Weave.Silo", "Weave.Silo.csproj")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate) || Directory.Exists(candidate))
                return candidate;
        }

        var exeDir = AppContext.BaseDirectory;
        var siloDll = Path.Combine(exeDir, "Weave.Silo.dll");
        if (File.Exists(siloDll))
            return siloDll;

        return null;
    }

    private static SiloArgs BuildSiloArgs(string siloPath, int port)
    {
        if (siloPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || Directory.Exists(siloPath))
        {
            var project = siloPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                ? siloPath
                : Path.Combine(siloPath, "Weave.Silo.csproj");

            return new SiloArgs("dotnet",
            [
                "run", "--project", project, "--",
                "--Weave:LocalMode=true",
                $"--urls=http://localhost:{port}"
            ]);
        }

        return new SiloArgs("dotnet",
        [
            siloPath,
            "--Weave:LocalMode=true",
            $"--urls=http://localhost:{port}"
        ]);
    }

    private sealed record SiloArgs(string FileName, List<string> Arguments);
}
