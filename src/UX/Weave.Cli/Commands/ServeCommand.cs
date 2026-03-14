using System.ComponentModel;
using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Weave.Cli.Commands;

public sealed class WorkspaceServeCommand : AsyncCommand<WorkspaceServeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--port <PORT>")]
        [Description("Port to listen on")]
        [DefaultValue(9401)]
        public int Port { get; init; } = 9401;

        [CommandOption("--background")]
        [Description("Run in the background")]
        [DefaultValue(false)]
        public bool Background { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // Check if silo is already running
        if (await IsReachableAsync(settings.Port, cancellationToken))
        {
            AnsiConsole.MarkupLine($"[yellow]Weave is already running on port {settings.Port}.[/]");
            return 0;
        }

        var siloPath = ResolveSiloPath();
        if (siloPath is null)
        {
            AnsiConsole.MarkupLine("[red]Could not locate the Weave silo.[/]");
            AnsiConsole.MarkupLine("Set [bold]WEAVE_SILO_PATH[/] to the silo project or published directory.");
            return 1;
        }

        var args = BuildSiloArgs(siloPath, settings.Port);

        if (settings.Background)
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
                AnsiConsole.MarkupLine("[red]Failed to start silo process.[/]");
                return 1;
            }

            // Wait briefly for startup
            await WaitForReadyAsync(settings.Port, cancellationToken);

            AnsiConsole.MarkupLine($"[green]Weave running in background (PID {process.Id}, port {settings.Port}).[/]");
            AnsiConsole.MarkupLine("  Local mode — no external services required.");
            AnsiConsole.MarkupLine($"  Stop with: [bold]weave serve stop[/] or terminate PID {process.Id}.");
            return 0;
        }

        // Foreground mode — exec the silo inline
        AnsiConsole.MarkupLine($"Starting Weave in local mode on port [bold]{settings.Port}[/]...");
        AnsiConsole.MarkupLine("  No external services required. Press Ctrl+C to stop.");
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
            AnsiConsole.MarkupLine("[red]Failed to start silo process.[/]");
            return 1;
        }

        await siloProcess.WaitForExitAsync(cancellationToken);
        return siloProcess.ExitCode;
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
        // 1. Explicit environment variable
        var envPath = Environment.GetEnvironmentVariable("WEAVE_SILO_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && (File.Exists(envPath) || Directory.Exists(envPath)))
            return envPath;

        // 2. Well-known development path (relative to workspace root)
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

        // 3. Adjacent published binary
        var exeDir = AppContext.BaseDirectory;
        var siloDll = Path.Combine(exeDir, "Weave.Silo.dll");
        if (File.Exists(siloDll))
            return siloDll;

        return null;
    }

    private static SiloArgs BuildSiloArgs(string siloPath, int port)
    {
        // If it's a .csproj or directory, use dotnet run
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

        // Published DLL
        return new SiloArgs("dotnet",
        [
            siloPath,
            "--Weave:LocalMode=true",
            $"--urls=http://localhost:{port}"
        ]);
    }

    private sealed record SiloArgs(string FileName, List<string> Arguments);
}
