using System.Diagnostics;
using Spectre.Console;

namespace Weave.Cli.Commands;

internal sealed class ServeCliCommand(SiloProcessService? silo = null) : ICliCommand<ServeOptions>
{
    private readonly SiloProcessService _silo = silo ?? new SiloProcessService();

    public string Name => "serve";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Start the local Weave server";

    public async Task<int> ExecuteAsync(ServeOptions options, CancellationToken ct)
    {
        var port = options.Port;
        var background = options.Background;

        if (await _silo.IsReachableAsync(port, ct))
        {
            CliTheme.WriteWarning($"Weave is already running on port {port}.");
            return 0;
        }

        var siloPath = WorkspaceSiloStarter.ResolveSiloPath();
        if (siloPath is null)
        {
            CliTheme.WriteError("Could not locate the Weave silo.");
            CliTheme.WriteMuted("  Run `weave init` to configure, or set WEAVE_SILO_PATH.");
            return 1;
        }

        var args = _silo.BuildSiloArgs(siloPath, port);

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

            // Drain both pipes to the Silo log so the child doesn't
            // block once its stdout/stderr buffer fills (verbose Orleans
            // startup can hit ~4 KB in seconds). See
            // docs/best-practices.md — "Background launchers must
            // drain pipes or not redirect."
            _silo.AttachLogDrainer(process, WorkspaceSiloStarter.GetSiloLogPath());

            await _silo.WaitForReadyAsync(port, ct, attempts: 30);

            CliTheme.WriteSuccess($"Weave running in background (PID {process.Id}, port {port}).");
            CliTheme.WriteMuted("  Local mode \u2014 no external services required.");
            CliTheme.WriteMuted($"  Stop with: weave serve stop or terminate PID {process.Id}.");
            CliTheme.WriteMuted($"  Logs: {WorkspaceSiloStarter.GetSiloLogPath()}");
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

        await siloProcess.WaitForExitAsync(ct);
        return siloProcess.ExitCode;
    }
}
