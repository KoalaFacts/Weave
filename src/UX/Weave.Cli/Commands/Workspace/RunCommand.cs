using System.CommandLine;
using System.Diagnostics;

namespace Weave.Cli.Commands;

internal static class RunCommand
{
    public static Command Create()
    {
        var nameArg = new Argument<string?>("name")
        {
            Description = "Workspace name (defaults to current directory)",
            DefaultValueFactory = _ => null,
            Arity = ArgumentArity.ZeroOrOne
        };
        var portOption = new Option<int>("--port")
        {
            Description = "Server port",
            DefaultValueFactory = _ => CliConfigStore.Load().DefaultPort
        };

        var cmd = new Command("run", "Start the server and workspace in one command") { nameArg, portOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArg);
            var port = parseResult.GetValue(portOption);
            return await new RunCliCommand().ExecuteAsync(new RunOptions(name, port), cancellationToken);
        });

        return cmd;
    }

    internal static Process? StartSilo(string siloPath, int port, Weave.Workspaces.Models.StorageConfig? workspaceStorage = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (siloPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || Directory.Exists(siloPath))
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

        if (!string.IsNullOrWhiteSpace(storageBackend) && storageBackend != "memory")
        {
            startInfo.ArgumentList.Add($"--Weave:Storage={storageBackend}");

            if (!string.IsNullOrWhiteSpace(storageConn))
            {
                var connKey = storageBackend switch
                {
                    "postgresql" or "postgres" => "PostgreSql",
                    "sqlserver" => "SqlServer",
                    "redis" => "Redis",
                    "sqlite" => "Sqlite",
                    _ => "Default"
                };
                startInfo.ArgumentList.Add($"--ConnectionStrings:{connKey}={storageConn}");
            }

            if (!string.IsNullOrWhiteSpace(storageSchema))
                startInfo.ArgumentList.Add($"--Weave:StorageSchema={storageSchema}");

            if (!string.IsNullOrWhiteSpace(workspaceStorage?.Database))
                startInfo.ArgumentList.Add($"--Weave:StorageDatabase={workspaceStorage.Database}");
        }

        // Auth config from CLI settings
        if (!string.IsNullOrWhiteSpace(cliConfig.AuthMode) && cliConfig.AuthMode != "none")
        {
            startInfo.ArgumentList.Add($"--Weave:Auth:Mode={cliConfig.AuthMode}");
            var resolvedAuth = CliConfigStore.ResolveConnectionString(cliConfig.AuthSecret);
            if (!string.IsNullOrWhiteSpace(resolvedAuth))
                startInfo.ArgumentList.Add($"--Weave:Auth:Secret={resolvedAuth}");
        }

        if (cliConfig.RequireHttps)
            startInfo.ArgumentList.Add("--Weave:RequireHttps=true");

        var process = Process.Start(startInfo);
        if (process is not null)
        {
            // Prevent Silo deadlock on a full stdout/stderr pipe by
            // routing both into the shared silo.log file. Same reason
            // as ServeCommand / UpCommand — we redirect to keep the
            // CLI's own output clean, so we MUST drain the pipes.
            WorkspaceServeCommand.AttachLogDrainer(process, WorkspaceSiloStarter.GetSiloLogPath());
        }
        return process;
    }

    internal static async Task<bool> IsReachableAsync(int port, CancellationToken ct)
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

    internal static async Task<bool> WaitForReadyAsync(int port, CancellationToken ct)
    {
        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(500, ct);
            if (await IsReachableAsync(port, ct))
                return true;
        }

        return false;
    }

    internal static void TryKill(Process? process)
    {
        if (process is null || process.HasExited)
            return;
        try
        { process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Process already exited between the HasExited check and Kill call — safe to ignore.
        }
    }
}
