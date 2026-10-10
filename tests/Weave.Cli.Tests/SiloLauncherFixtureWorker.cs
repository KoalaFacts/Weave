using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Weave.Cli.Tests;

internal sealed class SiloLauncherFixtureWorker : IAsyncDisposable
{
    private readonly string _root;
    private Process? _process;
    private bool _launchObserved;
    private readonly TcpListener _reservation = new(IPAddress.Loopback, 0);
    public string Executable { get; }
    public int Port { get; }

    public SiloLauncherFixtureWorker(string root, bool earlyExit)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The test worker requires a Linux executable script.");
        _root = root;
        _reservation.Start();
        Port = ((IPEndPoint)_reservation.LocalEndpoint).Port;
        Executable = Path.Join(root, "fixture-host");
        var script = "#!/usr/bin/env node\n" + "const earlyExit = " + (earlyExit ? "true" : "false") + ";\n" + Script;
        File.WriteAllText(Executable, script);
        File.SetUnixFileMode(Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void ReleasePort() => _reservation.Stop();

    public async Task ObserveStartedAsync(Task launch)
    {
        _launchObserved = true;
        var pidPath = Path.Join(_root, "worker.pid");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!File.Exists(pidPath))
        {
            if (launch.IsCompleted)
            {
                await launch;
                throw new InvalidOperationException("Launcher returned before the worker published its PID.");
            }
            await Task.Delay(20, timeout.Token);
        }
        _process = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pidPath, timeout.Token), CultureInfo.InvariantCulture));
        _process.HasExited.ShouldBeFalse();
    }

    public void AllowExit() => File.WriteAllText(Path.Join(_root, "exit"), "exit");

    public async ValueTask DisposeAsync()
    {
        _reservation.Stop();
        if (_process is null && _launchObserved)
        {
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var pidPath = Path.Join(_root, "worker.pid");
            while (!File.Exists(pidPath) && !recovery.IsCancellationRequested)
                await Task.Delay(20, CancellationToken.None);
            if (File.Exists(pidPath))
            {
                var pid = int.Parse(await File.ReadAllTextAsync(pidPath, CancellationToken.None), CultureInfo.InvariantCulture);
                try
                {
                    _process = Process.GetProcessById(pid);
                }
                catch (ArgumentException)
                {
                    // The worker exited before a process handle could be recovered.
                    return;
                }
            }
        }
        if (_process is null)
            return; // The parent owns the entire process group, including pre-PID startup failures.
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (_process.HasExited)
        {
            await _process.WaitForExitAsync(CancellationToken.None);
        }
        await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        _process.HasExited.ShouldBeTrue();
        _process.Dispose();
    }

    private const string Script = """
        const fs = require('node:fs');
        const http = require('node:http');
        const path = require('node:path');
        const root = process.cwd();
        const args = process.argv.slice(2);
        fs.writeFileSync(path.join(root, 'worker.args'), JSON.stringify(args));
        fs.writeFileSync(path.join(root, 'worker.pid.pending'), String(process.pid));
        fs.renameSync(path.join(root, 'worker.pid.pending'), path.join(root, 'worker.pid'));
        console.log('launcher-stdout-marker');
        console.error('launcher-stderr-marker');
        setTimeout(() => process.exit(91), 20000).unref();
        if (earlyExit) {
          const timer = setInterval(() => {
            if (fs.existsSync(path.join(root, 'exit'))) {
              clearInterval(timer);
              process.exit(23);
            }
          }, 10);
        } else {
          const urls = args.find(arg => arg.startsWith('--urls='));
          const port = Number(new URL(urls.substring('--urls='.length)).port);
          let healthRequests = 0;
          const server = http.createServer((request, response) => {
            if (request.url !== '/health') {
              response.writeHead(404).end();
              return;
            }
            healthRequests++;
            fs.appendFileSync(path.join(root, 'health-requests'), request.url + '\n');
            response.writeHead(healthRequests === 1 ? 503 : 200, { 'Content-Type': 'text/plain' });
            response.end('fixture-health');
          });
          server.listen(port, '127.0.0.1');
        }
        """;
}
