using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Weave.Cli.Tests;

internal sealed class LocalHostOwnedWorker : IAsyncDisposable
{
    private readonly string _root;
    private readonly TcpListener _reservation = new(IPAddress.Loopback, 0);
    private Process? _process;
    public string Executable { get; }
    public int Port { get; }
    public bool HasExited => _process.ShouldNotBeNull().HasExited;

    public LocalHostOwnedWorker(string root, string mode)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Owned executable worker requires Linux.");
        _root = root;
        Directory.CreateDirectory(root);
        _reservation.Start();
        Port = ((IPEndPoint)_reservation.LocalEndpoint).Port;
        Executable = Path.Join(root, "owned local host");
        File.WriteAllText(Executable, "#!/usr/bin/env node\nconst mode = '" + mode + "';\n" + Script);
        File.SetUnixFileMode(Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void ReleasePort() => _reservation.Stop();

    public async Task<JsonNode> ObserveStartedAsync(Task operation, CancellationToken ct)
    {
        while (!File.Exists(Path.Join(_root, "started.json")))
        {
            if (operation.IsCompleted)
            {
                await operation;
                throw new InvalidOperationException("Runner returned without starting the owned worker.");
            }
            await Task.Delay(10, ct);
        }
        var started = await ReadAsync("started.json", ct);
        _process = Process.GetProcessById(started["pid"].ShouldNotBeNull().GetValue<int>());
        File.WriteAllText(Path.Join(_root, "observed"), "observed");
        return started;
    }

    public async Task<JsonNode> ReadAsync(string name, CancellationToken ct)
    {
        var path = Path.Join(_root, name);
        while (!File.Exists(path))
            await Task.Delay(10, ct);
        return JsonNode.Parse(await File.ReadAllTextAsync(path, ct)).ShouldNotBeNull();
    }

    public async ValueTask DisposeAsync()
    {
        _reservation.Stop();
        if (_process is null)
            return; // The outer harness owns the child process group if observation failed.
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
        const atomic = (name, value) => {
          fs.writeFileSync(path.join(root, name + '.pending'), JSON.stringify(value));
          fs.renameSync(path.join(root, name + '.pending'), path.join(root, name));
        };
        const keys = ['Weave__Fixture', 'CapabilityTokens__Fixture', 'ConnectionStrings__Fixture',
          'ASPNETCORE_ENVIRONMENT', 'DOTNET_ENVIRONMENT', 'Urls', 'LOCAL_HOST_KEEP'];
        atomic('started.json', {pid: process.pid, args: process.argv.slice(2), cwd: process.cwd(),
          environment: Object.fromEntries(keys.filter(key => key in process.env).map(key => [key, process.env[key]]))});
        setTimeout(() => process.exit(92), 20000).unref();
        const write = (stream, value) => new Promise(resolve => stream.write(value, resolve));
        (async () => {
          await Promise.all([
            write(process.stdout, 'stdout fixture-local-operator-key fixture-local-signing-key\n' + 'o'.repeat(12000) + '\nstdout-drained\n'),
            write(process.stderr, 'stderr fixture-local-operator-key fixture-local-signing-key\n' + 'e'.repeat(12000) + '\nstderr-drained\n')
          ]);
          const url = new URL(process.argv.slice(2).find(value => value.startsWith('--urls=')).substring(7));
          const server = http.createServer((request, response) => {
            let body = '';
            request.on('data', chunk => { body += chunk; if (body.length > 4096) request.destroy(); });
            request.on('end', () => {
              atomic('request.json', {method: request.method, url: request.url, headers: request.headers, body});
              if (mode === 'pending') return;
              const timer = setInterval(() => {
                if (!fs.existsSync(path.join(root, 'observed'))) return;
                clearInterval(timer);
                response.writeHead(mode === 'denied' ? 403 : 204).end();
              }, 10);
              response.on('close', () => clearInterval(timer));
            });
          });
          server.listen(Number(url.port), '127.0.0.1');
        })().catch(error => { console.error(error.message); process.exit(93); });
        """;
}
