using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Weave.Tools.Tests;

// An actual bound socket: no free-port reservation race, retries, or HttpClient stub.
internal sealed class McpHttpTestPeer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _shutdown;
    private readonly Func<NetworkStream, CancellationToken, Task> _respond;
    private readonly Task _loop;

    public McpHttpTestPeer(Func<NetworkStream, CancellationToken, Task> respond)
    {
        _respond = respond;
        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        _shutdown.CancelAfter(TimeSpan.FromSeconds(15));
        _listener.Start();
        Endpoint = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/mcp";
        _loop = ServeAsync();
    }

    public string Endpoint { get; }
    public ConcurrentQueue<(string Headers, string Body)> Requests { get; } = new();

    private async Task ServeAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                await using var stream = client.GetStream();
                var bytes = new List<byte>();
                var next = new byte[1];
                while (true)
                {
                    var read = await stream.ReadAsync(next, _shutdown.Token);
                    if (read == 0)
                        throw new IOException("Peer received incomplete test request headers.");
                    bytes.Add(next[0]);
                    if (bytes.Count > 8192)
                        throw new IOException("Test request headers exceeded fixture bounds.");
                    if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10)
                        break;
                }
                var headers = Encoding.ASCII.GetString(bytes.ToArray());
                var length = 0;
                foreach (var line in headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        length = int.Parse(line[15..].Trim(), CultureInfo.InvariantCulture);
                if (length is < 0 or > 65536)
                    throw new IOException("Test request body exceeded fixture bounds.");
                var body = new byte[length];
                await stream.ReadExactlyAsync(body, _shutdown.Token);
                Requests.Enqueue((headers, Encoding.UTF8.GetString(body)));
                await _respond(stream, _shutdown.Token);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (IOException) when (_shutdown.IsCancellationRequested) { }
        catch (SocketException) when (_shutdown.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested) { return; }
    }

    public static async Task ReplyAsync(NetworkStream stream, CancellationToken ct, string body = "{}", string extraHeaders = "", int status = 200)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        await HeadersAsync(stream, ct, "application/json", bytes.Length, extraHeaders, status);
        await stream.WriteAsync(bytes, ct);
    }

    public static Task HeadersAsync(NetworkStream stream, CancellationToken ct, string contentType = "application/json", int length = 1, string extraHeaders = "", int status = 200)
    {
        var headers = $"HTTP/1.1 {status} Test\r\nContent-Type: {contentType}\r\nContent-Length: {length}\r\nConnection: close\r\n{extraHeaders}\r\n";
        return stream.WriteAsync(Encoding.ASCII.GetBytes(headers), ct).AsTask();
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        _listener.Stop();
        await _loop;
        _shutdown.Dispose();
    }
}
