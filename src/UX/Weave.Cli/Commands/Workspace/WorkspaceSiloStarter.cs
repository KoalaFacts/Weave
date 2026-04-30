using System.Diagnostics;

namespace Weave.Cli.Commands;

internal static class WorkspaceSiloStarter
{
    internal sealed record AutoStartResult(bool Success, string LogPath, string? Reason);

    internal static string GetSiloLogPath()
    {
        var weaveHome = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave");
        return Path.Combine(weaveHome, "silo.log");
    }

    internal static async Task<bool> AutoStartServeAsync(CancellationToken ct)
        => (await AutoStartServeWithDiagnosticsAsync(ct)).Success;

    internal static async Task<AutoStartResult> AutoStartServeWithDiagnosticsAsync(CancellationToken ct)
    {
        var logPath = GetSiloLogPath();
        var siloPath = ResolveSiloPath();
        if (siloPath is null)
            return new AutoStartResult(false, logPath, "Could not locate the Weave Silo on disk.");

        var config = CliConfigStore.Load();
        var port = config.DefaultPort;

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (siloPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || Directory.Exists(siloPath))
        {
            var project = siloPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                ? siloPath
                : Path.Combine(siloPath, "Weave.Silo.csproj");
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add("--project");
            startInfo.ArgumentList.Add(project);
            startInfo.ArgumentList.Add("--");
        }
        else
        {
            startInfo.ArgumentList.Add(siloPath);
        }

        startInfo.ArgumentList.Add("--Weave:LocalMode=true");
        startInfo.ArgumentList.Add($"--urls=http://localhost:{port}");

        if (!string.IsNullOrWhiteSpace(config.Storage) && config.Storage != "memory")
        {
            startInfo.ArgumentList.Add($"--Weave:Storage={config.Storage}");

            var resolvedConn = CliConfigStore.ResolveConnectionString(config.ConnectionString);
            if (!string.IsNullOrWhiteSpace(resolvedConn))
            {
                var connKey = config.Storage switch
                {
                    "postgresql" or "postgres" => "PostgreSql",
                    "sqlserver" => "SqlServer",
                    "redis" => "Redis",
                    _ => "Default"
                };
                startInfo.ArgumentList.Add($"--ConnectionStrings:{connKey}={resolvedConn}");
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        StreamWriter? logWriter = null;
        try
        {
            logWriter = new StreamWriter(logPath, append: false) { AutoFlush = true };
            logWriter.WriteLine($"=== Silo launch {DateTimeOffset.Now:o} ===");
            logWriter.WriteLine($"    cwd: {Environment.CurrentDirectory}");
            logWriter.WriteLine($"    args: {string.Join(' ', startInfo.ArgumentList)}");
        }
        catch (Exception ex)
        {
            logWriter?.Dispose();
            logWriter = null;
            Console.Error.WriteLine($"(silo log unavailable: {ex.Message})");
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            logWriter?.Dispose();
            return new AutoStartResult(false, logPath, $"Failed to launch dotnet: {ex.Message}");
        }

        if (process is null)
        {
            logWriter?.Dispose();
            return new AutoStartResult(false, logPath, "Process.Start returned null.");
        }

        if (logWriter is not null)
        {
            var writerLock = new object();
            void AppendLine(string prefix, string? line)
            {
                if (line is null)
                    return;
                lock (writerLock)
                {
                    try
                    {
                        logWriter.WriteLine($"{DateTime.Now:HH:mm:ss} {prefix} {line}");
                    }
                    catch
                    {
                        // writer may be disposed on exit
                    }
                }
            }

            process.OutputDataReceived += (_, e) => AppendLine("OUT", e.Data);
            process.ErrorDataReceived += (_, e) => AppendLine("ERR", e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (var i = 0; i < 120; i++)
        {
            await Task.Delay(500, ct);

            if (process.HasExited)
                return new AutoStartResult(
                    false,
                    logPath,
                    $"Silo process exited with code {process.ExitCode} during startup.");

            try
            {
                var response = await http.GetAsync($"http://localhost:{port}/health", ct);
                if (response.IsSuccessStatusCode)
                    return new AutoStartResult(true, logPath, null);
            }
            catch
            {
                // expected while the Silo is still warming up
            }
        }

        return new AutoStartResult(
            false,
            logPath,
            "Silo did not respond to /health within 60s.");
    }

    internal static string? ResolveSiloPath()
    {
        var envPath = Environment.GetEnvironmentVariable("WEAVE_SILO_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && (File.Exists(envPath) || Directory.Exists(envPath)))
            return envPath;

        var config = CliConfigStore.Load();
        if (!string.IsNullOrWhiteSpace(config.SiloPath) && (File.Exists(config.SiloPath) || Directory.Exists(config.SiloPath)))
            return config.SiloPath;

        var candidates = new[]
        {
            Path.Combine("src", "Runtime", "Weave.Silo"),
            Path.Combine("src", "Runtime", "Weave.Silo", "Weave.Silo.csproj")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate) || Directory.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        var exeDir = AppContext.BaseDirectory;
        var siloDll = Path.Combine(exeDir, "Weave.Silo.dll");
        return File.Exists(siloDll) ? siloDll : null;
    }
}
