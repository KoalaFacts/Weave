using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Weave.Cli.Tests;

internal sealed class StorageHealthEndpoint : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Socket _ipv6Reservation = new(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(45));
    private readonly Task<string> _request;

    public int Port { get; }

    public StorageHealthEndpoint(bool running)
    {
        try
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            // localhost can try either address family. Reserve the matching IPv6 port
            // without listening so the command can only reach our IPv4 HTTP endpoint.
            _ipv6Reservation.Bind(new IPEndPoint(IPAddress.IPv6Loopback, Port));
            _request = RespondAsync(running, _stop.Token);
        }
        catch
        {
            _listener.Stop();
            _ipv6Reservation.Dispose();
            _stop.Dispose();
            throw;
        }
    }

    public async Task AssertHealthRequestedAsync() =>
        (await _request.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))
            .ShouldBe("GET /health HTTP/1.1");

    private async Task<string> RespondAsync(bool running, CancellationToken cancellationToken)
    {
        using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
        var request = (await reader.ReadLineAsync(cancellationToken)).ShouldNotBeNull();
        while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 })
        {
            // Drain the headers before responding to the one owned health request.
        }
        var status = running ? "200 OK" : "503 Service Unavailable";
        var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        return request;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _request.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // An early assertion failure can leave the owned accept or read pending.
        }
        finally
        {
            _listener.Stop();
            _ipv6Reservation.Dispose();
            _stop.Dispose();
        }
    }
}
