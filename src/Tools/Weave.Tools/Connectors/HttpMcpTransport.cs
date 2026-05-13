using System.Net.Http.Headers;
using System.Text;
using System.Threading.Channels;
using Weave.Workspaces.Manifest;

namespace Weave.Tools.Connectors;

// Streamable HTTP transport for MCP (spec rev 2025-03-26). The client
// advertises `Accept: application/json, text/event-stream` as the spec
// requires; the server picks. For JSON responses we enqueue the body
// verbatim. For `text/event-stream` we parse SSE frames and enqueue each
// `data:` payload as its own message — the McpConnection read loop then
// dispatches them in order (notifications are logged-and-dropped, the
// final response matches the pending request id).
//
// Out of scope today: the optional GET /mcp server-initiated SSE channel
// and MCP session-id headers.
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
            // Spec: client MUST advertise both content types.
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
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
            if (string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                await _incoming.Writer.WriteAsync(body, ct);
                return;
            }
            if (string.Equals(contentType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                await ConsumeEventStreamAsync(response, ct);
                return;
            }

            _lastDiagnostic = $"unexpected Content-Type '{contentType}' (expected application/json or text/event-stream)";
            throw new IOException(_lastDiagnostic);
        }
    }

    private async Task ConsumeEventStreamAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var dataBuf = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
                break;

            if (line.Length == 0)
            {
                if (dataBuf.Length > 0)
                {
                    await _incoming.Writer.WriteAsync(dataBuf.ToString(), ct);
                    dataBuf.Clear();
                }
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                // Per SSE spec: strip exactly one optional leading space after the colon.
                var payload = line.Length > 5 && line[5] == ' ' ? line.AsSpan(6) : line.AsSpan(5);
                if (dataBuf.Length > 0) dataBuf.Append('\n');
                dataBuf.Append(payload);
            }
            // Other SSE fields (event:, id:, retry:, : comments) ignored — MCP only uses data:.
        }

        // Flush a trailing event terminated by EOF rather than a blank line.
        if (dataBuf.Length > 0)
            await _incoming.Writer.WriteAsync(dataBuf.ToString(), ct);
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
