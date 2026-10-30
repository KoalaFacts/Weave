using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Weave.Cli.Tests;

internal sealed class RunLoopbackSilo : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task _stdout;
    private readonly Task<string> _stderr;
    private readonly TaskCompletionSource<int> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _health = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<JsonObject> _start = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentQueue<string> Requests { get; } = new();
    public bool HasExited => _process.HasExited;

    public RunLoopbackSilo(string root, string mode)
    {
        var script = Path.Join(root, "run-loopback-silo.cjs");
        File.WriteAllText(script, Script);
        var start = new ProcessStartInfo("node")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(mode);
        _process = Process.Start(start).ShouldNotBeNull();
        _process.StandardInput.Close();
        _stdout = ReadEventsAsync();
        _stderr = DrainAsync(_process.StandardError);
    }

    public Task<int> ReadyAsync(CancellationToken ct) => _ready.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
    public Task HealthObservedAsync(CancellationToken ct) => _health.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
    public Task<JsonObject> StartObservedAsync(CancellationToken ct) => _start.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

    private async Task ReadEventsAsync()
    {
        while (await _process.StandardOutput.ReadLineAsync() is { } line)
        {
            var message = JsonNode.Parse(line).ShouldNotBeNull();
            if (message["port"] is { } port)
                _ready.TrySetResult(port.GetValue<int>());
            else
            {
                Requests.Enqueue(message["method"].ShouldNotBeNull().GetValue<string>() + " "
                    + message["path"].ShouldNotBeNull().GetValue<string>());
                if (message["path"].ShouldNotBeNull().GetValue<string>() == "/health")
                    _health.TrySetResult();
                if (message["path"].ShouldNotBeNull().GetValue<string>() == "/api/workspaces")
                    _start.TrySetResult(message.AsObject());
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
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
            await Task.WhenAll(_process.WaitForExitAsync(CancellationToken.None), _stdout, _stderr)
                .WaitAsync(TimeSpan.FromSeconds(5));
            (await _stderr).ShouldBeEmpty();
        }
        finally
        {
            _process.Dispose();
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader)
    {
        var retained = new StringBuilder();
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory())) != 0)
        {
            retained.Append(buffer, 0, count);
            if (retained.Length > 8192)
                retained.Remove(0, retained.Length - 8192);
        }
        return retained.ToString();
    }

    private const string Script = """
        const http = require('node:http');
        const mode = process.argv[2];
        const emit = data => process.stdout.write(JSON.stringify(data) + '\n');
        const server = http.createServer((request, response) => {
          let body = '';
          request.on('data', chunk => {
            body += chunk;
            if (body.length > 65536) request.destroy();
          });
          request.on('end', () => {
            emit({method: request.method, path: request.url, headers: request.headers, body: body ? JSON.parse(body) : null});
            if (request.url === '/health') {
              response.writeHead(mode === 'missing-host' ? 503 : 200).end();
            } else if (request.method === 'POST' && request.url === '/api/workspaces') {
              if (mode === 'cancel-pending') return;
              if (mode === 'rejected') {
                response.writeHead(403).end();
              } else {
                response.writeHead(201, {'Content-Type': 'application/json'});
                response.end(JSON.stringify({workspaceId:'run-workspace-id',name:'run-workspace',status:'Running',recoveryCondition:'StartedOnThisHost',containerCount:0}));
              }
            } else {
              response.writeHead(404).end();
            }
          });
        });
        server.listen(0, '127.0.0.1', () => emit({port:server.address().port}));
        setTimeout(() => process.exit(92), 25000).unref();
        """;
}
