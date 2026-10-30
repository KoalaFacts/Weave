using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Weave.Cli.Tests;

internal sealed class LocalCliLoopbackServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
    private readonly Task _serving;
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Port { get; }
    public List<Dictionary<string, string>> Headers { get; } = [];
    public TaskCompletionSource RequestObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public LocalCliLoopbackServer(params LocalCliHttpStep[] steps)
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _stop.CancelAfter(TimeSpan.FromSeconds(12));
        _serving = ServeAsync(steps);
    }

    public async Task CompleteAsync()
    {
        await Task.WhenAny(_completed.Task, _serving);
        if (_serving.IsCompleted)
            await _serving;
        await _completed.Task;
    }

    private async Task ServeAsync(LocalCliHttpStep[] steps)
    {
        foreach (var step in steps)
        {
            using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            await using var stream = client.GetStream();
            var header = new StringBuilder();
            var next = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                await stream.ReadExactlyAsync(next, _stop.Token);
                header.Append((char)next[0]);
                header.Length.ShouldBeLessThan(16384);
            }
            var lines = header.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            lines[0].ShouldBe($"{step.Method} {step.Path} HTTP/1.1");
            var headers = lines.Skip(1).Select(line => line.Split(':', 2))
                .ToDictionary(parts => parts[0], parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
            headers.ContainsKey("Transfer-Encoding").ShouldBeFalse();
            if (headers.TryGetValue("Content-Length", out var length))
                length.ShouldBe("0");
            Headers.Add(headers);
            RequestObserved.TrySetResult();
            if (step.Status == 0)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, _stop.Token);
                return;
            }
            var data = Encoding.UTF8.GetBytes(step.Body);
            var contentType = step.Credential ? "text/plain" : "application/json";
            var cache = step.Credential ? "Cache-Control: no-store\r\n" : "";
            var response = $"HTTP/1.1 {step.Status.ToString(CultureInfo.InvariantCulture)} Fixture\r\nContent-Type: {contentType}\r\n{cache}Content-Length: {data.Length.ToString(CultureInfo.InvariantCulture)}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
            await stream.WriteAsync(data, _stop.Token);
        }
        _completed.TrySetResult();
        using var unexpected = await _listener.AcceptTcpClientAsync(_stop.Token);
        throw new InvalidOperationException("The command sent an unexpected request after the planned HTTP sequence.");
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try
        {
            try
            { await _serving.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                _stop.Token.IsCancellationRequested.ShouldBeTrue();
            }
        }
        finally
        {
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
