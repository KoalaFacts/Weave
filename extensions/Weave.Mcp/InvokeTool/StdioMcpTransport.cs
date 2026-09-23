using System.Collections.Concurrent;
using System.Diagnostics;
using Weave.Workspaces.Manifest;

namespace Weave.Tools.Connectors;

internal sealed class StdioMcpTransport : IMcpTransport
{
    private const int MaxStderrTailLines = 50;

    private readonly Process _process;
    private readonly ConcurrentQueue<string> _stderrTail;

    private StdioMcpTransport(Process process, ConcurrentQueue<string> stderrTail)
    {
        _process = process;
        _stderrTail = stderrTail;
    }

    public bool HasExited => _process.HasExited;

    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    public static Task<IMcpTransport> ConnectAsync(McpConfig config, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = config.Server,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in config.Args)
            psi.ArgumentList.Add(arg);

        foreach (var (key, value) in config.Env)
            psi.Environment[key] = value;

        var process = new Process { StartInfo = psi };

        // Drain stderr into a bounded ring so a dead-process error can
        // attach diagnostic context. Without this, verbose MCP servers
        // hang once the ~4 KB pipe buffer fills.
        var stderrTail = new ConcurrentQueue<string>();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            stderrTail.Enqueue(e.Data);
            while (stderrTail.Count > MaxStderrTailLines)
                stderrTail.TryDequeue(out string? _);
        };

        process.Start();
        process.BeginErrorReadLine();

        return Task.FromResult<IMcpTransport>(new StdioMcpTransport(process, stderrTail));
    }

    public async Task SendAsync(string json, CancellationToken ct)
    {
        await _process.StandardInput.WriteLineAsync(json.AsMemory(), ct);
        await _process.StandardInput.FlushAsync(ct);
    }

    public Task<string?> ReceiveAsync(CancellationToken ct) =>
        _process.StandardOutput.ReadLineAsync(ct).AsTask();

    public string FormatDiagnosticTail()
    {
        if (_stderrTail.IsEmpty)
            return string.Empty;
        var lines = _stderrTail.ToArray();
        return $"stderr tail: {string.Join(" | ", lines[^Math.Min(5, lines.Length)..])}";
    }

    public ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            try
            { _process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* already exited */ }
        }
        _process.Dispose();
        return ValueTask.CompletedTask;
    }
}
