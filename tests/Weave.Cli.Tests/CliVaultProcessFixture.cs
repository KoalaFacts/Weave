using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Weave.Cli.Tests;

internal static class CliVaultProcessFixture
{
    public static Task RunAsync(Type testClass, string secretPath, string responseBody, int status, Action<CliSecretResolver> assertion) =>
        SiloLauncherProcessHarness.RunAsync(testClass, async _ =>
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var request = ReplyAsync(listener, responseBody, status, serverCancellation.Token);
            var previousAddress = Environment.GetEnvironmentVariable("VAULT_ADDR");
            var previousToken = Environment.GetEnvironmentVariable("VAULT_TOKEN");
            try
            {
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Environment.SetEnvironmentVariable("VAULT_ADDR", $"http://127.0.0.1:{port}/");
                Environment.SetEnvironmentVariable("VAULT_TOKEN", "fake-vault-token-for-owned-fixture");

                await Task.Run(() => assertion(new CliSecretResolver()), TestContext.Current.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                var observed = await request.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

                observed.RequestLine.ShouldBe("GET /v1/" + secretPath + " HTTP/1.1");
                observed.Token.ShouldBe("fake-vault-token-for-owned-fixture");
            }
            finally
            {
                Environment.SetEnvironmentVariable("VAULT_ADDR", previousAddress);
                Environment.SetEnvironmentVariable("VAULT_TOKEN", previousToken);
                await serverCancellation.CancelAsync();
                try
                {
                    await request.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (OperationCanceledException) when (serverCancellation.IsCancellationRequested)
                {
                    // Cancelling the owned listener ends an outstanding accept or read after a failed assertion.
                }
                finally
                {
                    listener.Stop();
                }
            }
        });

    private static async Task<(string RequestLine, string? Token)> ReplyAsync(
        TcpListener listener, string body, int status, CancellationToken cancellationToken)
    {
        using var socket = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = socket.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
        var requestLine = (await reader.ReadLineAsync(cancellationToken)).ShouldNotBeNull();
        string? token = null;
        while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 } header)
        {
            if (header.StartsWith("X-Vault-Token:", StringComparison.OrdinalIgnoreCase))
                token = header["X-Vault-Token:".Length..].Trim();
        }
        var payload = Encoding.UTF8.GetBytes(body);
        var headerBytes = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Forbidden")}\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headerBytes, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        return (requestLine, token);
    }
}
