using System.CommandLine;
using System.Diagnostics;

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
            return await new ServeCliCommand().ExecuteAsync(new ServeOptions(port, background), cancellationToken);
        });

        return cmd;
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

    internal static async Task WaitForReadyAsync(int port, CancellationToken ct)
    {
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(500, ct);
            if (await IsReachableAsync(port, ct))
                return;
        }
    }

    internal static SiloArgs BuildSiloArgs(string siloPath, int port)
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

    internal sealed record SiloArgs(string FileName, List<string> Arguments);

    // Shared drainer used by any background silo launcher. Writes both
    // stdout and stderr lines to the given log file so the Silo can
    // emit as much as it wants without filling the OS pipe buffer.
    internal static void AttachLogDrainer(Process process, string logPath)
    {
        try
        { Directory.CreateDirectory(Path.GetDirectoryName(logPath)!); }
        catch { /* best-effort — read-only home directory */ }

        StreamWriter? writer = null;
        try
        { writer = new StreamWriter(logPath, append: true) { AutoFlush = true }; }
        catch { /* log path unavailable — drop through and still drain */ }

        var sink = writer;
        var gate = new object();

        void Append(string prefix, string? line)
        {
            if (line is null)
                return;
            if (sink is null)
                return;
            lock (gate)
            {
                try
                { sink.WriteLine($"{DateTime.Now:HH:mm:ss} {prefix} {line}"); }
                catch { /* writer disposed during shutdown */ }
            }
        }

        process.OutputDataReceived += (_, e) => Append("OUT", e.Data);
        process.ErrorDataReceived += (_, e) => Append("ERR", e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }
}
