using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Weave.Cli.Tests;

internal sealed class SiloLifecycleWorker : IAsyncDisposable
{
    private readonly string _root;
    private readonly TcpListener _reservation = new(IPAddress.Loopback, 0);
    private Process? _process;
    public string Executable { get; }
    public int Port { get; }
    public bool HasExited => _process.ShouldNotBeNull().HasExited;

    public SiloLifecycleWorker(string root, string mode)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Lifecycle fixture requires Linux executable scripts.");
        _root = root;
        Directory.CreateDirectory(root);
        _reservation.Start();
        Port = ((IPEndPoint)_reservation.LocalEndpoint).Port;
        Executable = Path.Join(root, "owned fixture-host");
        File.WriteAllText(Executable, "#!/usr/bin/env node\nconst mode = '" + mode + "';\n" + Script);
        File.SetUnixFileMode(Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void ReleasePort() => _reservation.Stop();

    public void Attach(Process process)
    {
        _process = process;
        File.WriteAllText(Path.Join(_root, "observed"), "observed");
    }

    public async Task ObserveStartedAsync(Task operation, CancellationToken ct)
    {
        var path = Path.Join(_root, "started.json");
        while (!File.Exists(path))
        {
            if (operation.IsCompleted)
            {
                await operation;
                throw new InvalidOperationException("Command finished before the owned worker started.");
            }
            await Task.Delay(10, ct);
        }
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path, ct)).ShouldNotBeNull();
        Attach(Process.GetProcessById(json["pid"].ShouldNotBeNull().GetValue<int>()));
    }

    public async Task<string[]> ArgumentsAsync(CancellationToken ct)
    {
        var path = Path.Join(_root, "started.json");
        while (!File.Exists(path))
            await Task.Delay(10, ct);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path, ct)).ShouldNotBeNull();
        return json["args"].ShouldNotBeNull().AsArray().Select(value => value.ShouldNotBeNull().GetValue<string>()).ToArray();
    }

    public async Task<JsonNode> RequestAsync(CancellationToken ct)
    {
        var path = Path.Join(_root, "request.json");
        while (!File.Exists(path))
            await Task.Delay(10, ct);
        return JsonNode.Parse(await File.ReadAllTextAsync(path, ct)).ShouldNotBeNull();
    }

    public async Task ListeningAsync(CancellationToken ct)
    {
        while (!File.Exists(Path.Join(_root, "listening.json")))
            await Task.Delay(10, ct);
    }

    public async Task<JsonNode> HealthObservedAsync(CancellationToken ct)
    {
        var path = Path.Join(_root, "health.json");
        while (!File.Exists(path))
            await Task.Delay(10, ct);
        return JsonNode.Parse(await File.ReadAllTextAsync(path, ct)).ShouldNotBeNull();
    }

    public int ExitCode => _process.ShouldNotBeNull().ExitCode;

    public void AllowExit() => File.WriteAllText(Path.Join(_root, "exit"), "exit");

    public Task WaitForExitAsync(CancellationToken ct) => _process.ShouldNotBeNull().WaitForExitAsync(ct);

    public async ValueTask DisposeAsync()
    {
        _reservation.Stop();
        if (_process is null)
            return; // The outer harness owns and kills the entire child group if observation failed.
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (_process.HasExited)
        {
            await _process.WaitForExitAsync(CancellationToken.None);
        }
        try
        {
            await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            _process.Dispose();
        }
    }

    private const string Script = """
        const fs = require('node:fs');
        const path = require('node:path');
        const http = require('node:http');
        const root = __dirname;
        const atomic = (name, data) => {
          fs.writeFileSync(path.join(root, name + '.pending'), JSON.stringify(data));
          fs.renameSync(path.join(root, name + '.pending'), path.join(root, name));
        };
        atomic('started.json', {pid: process.pid, args: process.argv.slice(2)});
        setTimeout(() => process.exit(92), 20000).unref();
        const write = (stream, data) => new Promise(resolve => stream.write(data, resolve));
        (async () => {
          await Promise.all([
            write(process.stdout, 'lifecycle-stdout-marker\n' + 'o'.repeat(98304) + '\nstdout-end\n'),
            write(process.stderr, 'lifecycle-stderr-marker\n' + 'e'.repeat(98304) + '\nstderr-end\n')
          ]);
          if (mode === 'exit') {
            const timer = setInterval(() => {
              if (fs.existsSync(path.join(root, 'exit'))) {
                clearInterval(timer);
                process.exit(23);
              }
            }, 10);
            return;
          }
          const url = process.argv.slice(2).find(value => value.startsWith('--urls='));
          const port = Number(new URL(url.substring('--urls='.length)).port);
          const server = http.createServer((request, response) => {
            if (request.url === '/health') {
              if (!fs.existsSync(path.join(root, 'observed'))) {
                response.writeHead(503).end();
                return;
              }
              const status = mode === 'unhealthy' || mode === 'exit-on-probe' ? 503 : 200;
              atomic('health.json', {status});
              response.writeHead(status).end(() => { if (mode === 'exit-on-probe') process.exit(23); });
              return;
            }
            if (request.method !== 'POST' || request.url !== '/api/workspaces') {
              response.writeHead(404).end();
              return;
            }
            let body = '';
            request.on('data', chunk => { body += chunk; if (body.length > 65536) request.destroy(); });
            request.on('end', () => {
              atomic('request.json', {headers: request.headers, body: JSON.parse(body)});
              if (mode === 'reject') {
                response.writeHead(403).end();
                return;
              }
              response.writeHead(201, {'Content-Type':'application/json'});
              response.end(JSON.stringify({workspaceId:'owned-run-id',name:'owned-run',status:'Running',recoveryCondition:'StartedOnThisHost',containerCount:0}));
            });
          });
          server.listen(port, '127.0.0.1', () => atomic('listening.json', {port}));
        })().catch(error => { console.error(error.message); process.exit(93); });
        """;
}
