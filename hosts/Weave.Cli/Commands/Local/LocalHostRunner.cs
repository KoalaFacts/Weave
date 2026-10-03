using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Weave.Cli.Commands.Local;

internal sealed class LocalHostRunner(ILocalDeploymentStore store, TimeProvider clock)
{
    public static void RequireAvailablePorts(int httpPort)
    {
        foreach (var port in new[] { httpPort, 11111, 30000 }.Distinct())
        {
            try
            {
                using var probe = new TcpListener(IPAddress.Loopback, port);
                probe.Start();
                probe.Stop();
            }
            catch (SocketException)
            {
                throw new ArgumentException($"Local port {port} is already in use. Stop the existing Host before starting another; its data was preserved.");
            }
        }
    }

    public async Task<int> RunAsync(string directory, LocalDeployment deployment, bool initialize, CancellationToken ct)
    {
        RequireAvailablePorts(deployment.Port);
        var launch = SiloProcessService.BuildSiloArgs(deployment.HostPath, deployment.Port);
        var info = new ProcessStartInfo(launch.FileName)
        {
            WorkingDirectory = Path.Combine(directory, "private"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in launch.Arguments)
            info.ArgumentList.Add(argument.StartsWith("--urls=", StringComparison.Ordinal) ? "--urls=" + deployment.Origin : argument);
        info.ArgumentList.Add("--contentRoot=" + info.WorkingDirectory);
        foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("Weave", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("CapabilityTokens", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("ConnectionStrings", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("ASPNETCORE", StringComparison.OrdinalIgnoreCase)
            || key.Equals("DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase)
            || key.Equals("Urls", StringComparison.OrdinalIgnoreCase)).ToArray())
            info.Environment.Remove(key);
        info.Environment["DOTNET_ENVIRONMENT"] = "Production";
        using var process = Process.Start(info) ?? throw new IOException("Published Host did not start.");
        // The Host is trusted executable code. Bound diagnostics and redact its protected configuration.
        var secrets = new[] { store.OperatorKey(directory), store.SigningKey(directory) };
        var drains = Task.WhenAll(DrainAsync(process.StandardOutput, secrets), DrainAsync(process.StandardError, secrets));
        try
        {
            using var client = LocalHttp.CreateClient(deployment.Origin);
            var http = new LocalHttp(client, clock);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            try
            { await ConnectAsync(process, http, store.OperatorKey(directory), startup.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IOException("Host did not establish the governed document connection within 30 seconds.");
            }
            if (initialize)
            {
                if (!store.CompleteInitialization(directory, deployment))
                {
                    Console.Error.WriteLine("Initialization did not establish both durable stores. No ready configuration was saved.");
                    return 1;
                }
                Console.WriteLine("Local document approval configured. Credentials remain private; all writes require human approval.");
                return 0;
            }
            Console.WriteLine($"Host ready at {deployment.Origin}. Keep this terminal open; Ctrl+C stops this Host.");
            Console.WriteLine("In another terminal: weave local codex. For a Pending UUID: weave local review --id UUID.");
            await process.WaitForExitAsync(ct);
            return process.ExitCode;
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await drains;
        }
    }

    private async Task ConnectAsync(Process process, LocalHttp http, string key, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw new IOException("Host exited before the governed document connection was ready.");
            try
            {
                var result = await http.CallAsync(HttpMethod.Post, "/api/operator/tools/files/connect", null, key, null, ct);
                if (result.Status == 204)
                    return;
                throw new IOException($"Document connection was denied (HTTP {result.Status}). This local setup is not ready.");
            }
            catch (HttpRequestException) when (!process.HasExited)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), clock, ct);
            }
        }
        throw new IOException("Host was not ready within the startup limit.");
    }

    private static async Task DrainAsync(StreamReader reader, string[] secrets)
    {
        while (await LocalTextLines.ReadAsync(reader, 4096, discardExcess: true, CancellationToken.None) is { } line)
        {
            foreach (var secret in secrets)
                line = line.Replace(secret, "[redacted]", StringComparison.Ordinal);
            await Console.Error.WriteLineAsync(LocalReview.Display(line));
        }
    }
}
