using System.Collections.ObjectModel;
using System.Diagnostics;

namespace Weave.Cli.Commands;

internal static class SiloProcessService
{
    public static Process? StartSilo(string siloPath, int port, Weave.Workspaces.Manifest.StorageConfig? workspaceStorage = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        AddSiloArguments(startInfo.ArgumentList, siloPath, port);
        AddStorageArguments(startInfo.ArgumentList, workspaceStorage);
        AddAuthArguments(startInfo.ArgumentList);

        var process = Process.Start(startInfo);
        if (process is not null)
            AttachLogDrainer(process, WorkspaceSiloPaths.GetSiloLogPath());

        return process;
    }

    public static async Task<bool> IsReachableAsync(int port, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var response = await http.GetAsync($"http://localhost:{port}/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    public static async Task<bool> WaitForReadyAsync(int port, CancellationToken ct, int attempts = 60)
    {
        for (var i = 0; i < attempts; i++)
        {
            await Task.Delay(500, ct);
            if (await IsReachableAsync(port, ct))
                return true;
        }

        return false;
    }

    public static SiloArgs BuildSiloArgs(string siloPath, int port)
    {
        if (siloPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || Directory.Exists(siloPath))
        {
            var project = siloPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                ? siloPath
                : Path.Combine(siloPath, "Weave.Silo.csproj");

            return new SiloArgs("dotnet", ["run", "--project", project, "--", "--Weave:LocalMode=true", $"--urls=http://localhost:{port}"]);
        }

        return new SiloArgs("dotnet", [siloPath, "--Weave:LocalMode=true", $"--urls=http://localhost:{port}"]);
    }

    public static void TryKill(Process? process)
    {
        if (process is null || process.HasExited)
            return;
        try
        { process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    public static void AttachLogDrainer(Process process, string logPath)
    {
        try
        { Directory.CreateDirectory(Path.GetDirectoryName(logPath)!); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"(silo log directory unavailable: {ex.Message})");
        }

        StreamWriter? writer = null;
        try
        { writer = new StreamWriter(logPath, append: true) { AutoFlush = true }; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"(silo log file unavailable: {ex.Message})");
        }

        var sink = writer;
        var gate = new object();

        void Append(string prefix, string? line)
        {
            if (line is null || sink is null)
                return;
            lock (gate)
            {
                try
                { sink.WriteLine($"{DateTime.Now:HH:mm:ss} {prefix} {line}"); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // Log sink is dead — continue draining the pipe so
                    // the child process doesn't block on a full buffer.
                }
            }
        }

        process.OutputDataReceived += (_, e) => Append("OUT", e.Data);
        process.ErrorDataReceived += (_, e) => Append("ERR", e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    private static void AddSiloArguments(Collection<string> args, string siloPath, int port)
    {
        var siloArgs = BuildSiloArgs(siloPath, port);
        foreach (var arg in siloArgs.Arguments)
            args.Add(arg);
    }

    private static void AddStorageArguments(Collection<string> args, Weave.Workspaces.Manifest.StorageConfig? workspaceStorage)
    {
        var storageBackend = workspaceStorage?.Backend;
        var storageConn = workspaceStorage?.ConnectionString;
        var storageSchema = workspaceStorage?.Schema;
        var cliConfig = CliConfigStore.Load();

        if (string.IsNullOrWhiteSpace(storageBackend))
        {
            storageBackend = cliConfig.Storage;
            storageConn = CliConfigStore.ResolveConnectionString(cliConfig.ConnectionString);
        }
        else if (!string.IsNullOrWhiteSpace(storageConn))
        {
            storageConn = CliConfigStore.ResolveConnectionString(storageConn);
        }

        if (string.IsNullOrWhiteSpace(storageBackend) || storageBackend == "memory")
            return;

        args.Add($"--Weave:ActorStorage:Provider={storageBackend}");
        if (!string.IsNullOrWhiteSpace(storageConn))
            args.Add($"--ConnectionStrings:{ConnectionName(storageBackend)}={storageConn}");
        if (!string.IsNullOrWhiteSpace(storageSchema))
            args.Add($"--Weave:ActorStorage:Schema={storageSchema}");
        if (!string.IsNullOrWhiteSpace(workspaceStorage?.Database))
            args.Add($"--Weave:ActorStorage:Database={workspaceStorage.Database}");
    }

    private static void AddAuthArguments(Collection<string> args)
    {
        var cliConfig = CliConfigStore.Load();
        if (!string.IsNullOrWhiteSpace(cliConfig.AuthMode) && cliConfig.AuthMode != "none")
        {
            args.Add($"--Weave:Auth:Mode={cliConfig.AuthMode}");
            var resolvedAuth = CliConfigStore.ResolveConnectionString(cliConfig.AuthSecret);
            if (!string.IsNullOrWhiteSpace(resolvedAuth))
                args.Add($"--Weave:Auth:Secret={resolvedAuth}");
        }

        if (cliConfig.RequireHttps)
            args.Add("--Weave:RequireHttps=true");
    }

    private static string ConnectionName(string storageBackend) => storageBackend switch
    {
        "postgresql" => "PostgreSql",
        "sqlserver" => "SqlServer",
        "redis" => "Redis",
        "sqlite" => "Sqlite",
        _ => "Default"
    };
}
