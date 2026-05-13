using System.Net.Http.Headers;
using System.Text;
using System.Threading.Channels;
using Weave.Workspaces.Manifest;

namespace Weave.Tools.Connectors;

// Streamable HTTP transport for MCP (spec rev 2025-03-26), synchronous-JSON
// subset. Each request is POSTed; the JSON response is enqueued so the
// McpConnection's read loop can pick it up unchanged. Notifications (no
// `id`) POST and expect 204 No Content — nothing enqueued.
//
// Out of scope today: consuming server-sent SSE streams on POST responses,
// the optional GET /mcp server-initiated SSE channel, session ID headers.
// The echo-mcp HTTP server exposes those for forward compatibility, but
// Weave's connector doesn't drive them yet.
internal sealed class HttpMcpTransport : IMcpTransport
{
    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();
    private string? _lastDiagnostic;
    private bool _disposed;

    private HttpMcpTransport(HttpClient httpClient, Uri endpoint)
    {
        _httpClient = httpClient;
        _endpoint = endpoint;
    }

    public bool HasExited => _disposed;

    public int? ExitCode => null;

    public static Task<IMcpTransport> ConnectAsync(McpConfig config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.Url))
            throw new InvalidOperationException("HttpMcpTransport requires McpConfig.Url");

        if (!Uri.TryCreate(config.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"McpConfig.Url '{config.Url}' is not an absolute http(s) URL.");
        }

        return Task.FromResult<IMcpTransport>(new HttpMcpTransport(new HttpClient(), uri));
    }

    public async Task SendAsync(string json, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // HttpRequestException isn't in McpToolConnector.InvokeAsync's catch
        // filter (which expects IO-style exceptions), so wrap network/transport
        // failures as IOException at this boundary — keeps the abstraction
        // honest and lets the connector surface them as ToolResult.Error.
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            response = await _httpClient.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            _lastDiagnostic = ex.Message;
            throw new IOException($"HTTP transport send failed: {ex.Message}", ex);
        }

        using (response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
                return; // server accepted a notification; no body to enqueue

            if (!response.IsSuccessStatusCode)
            {
                _lastDiagnostic = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                throw new IOException(_lastDiagnostic);
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                _lastDiagnostic = $"unexpected Content-Type '{contentType}' (this transport only consumes application/json today)";
                throw new IOException(_lastDiagnostic);
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            await _incoming.Writer.WriteAsync(body, ct);
        }
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        try { return await _incoming.Reader.ReadAsync(ct); }
        catch (ChannelClosedException) { return null; }
    }

    public string FormatDiagnosticTail() =>
        _lastDiagnostic is null ? string.Empty : $"last HTTP error: {_lastDiagnostic}";

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _incoming.Writer.TryComplete();
        _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }
}
