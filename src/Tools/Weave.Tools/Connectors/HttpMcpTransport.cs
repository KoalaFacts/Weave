using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Channels;
using Weave.Workspaces.Manifest;

namespace Weave.Tools.Connectors;

// Streamable HTTP transport for MCP (spec rev 2025-03-26).
//
// Security posture: the peer is treated as hostile.
//   - SSRF: literal loopback / private / link-local / reserved IPs are
//     rejected unless McpConfig.AllowPrivateEndpoints is explicitly true.
//     URLs with userinfo or fragments are always rejected.
//   - Resource bounds: MaxFrameBytes caps a single response or SSE data
//     value; MaxResponseBytes caps total bytes read for one request;
//     MaxQueuedFrames bounds the in-memory channel.
//   - Timeouts: HttpClient.Timeout caps the whole request; IdleTimeoutSeconds
//     caps the gap between SSE frames (slow-loris defense).
//   - Diagnostics never leak raw server bodies; only status codes,
//     truncated reasons, and counts.
//
// Out of scope today: the optional GET /mcp server-initiated SSE channel,
// MCP session-id headers, Authorization header pass-through.
internal sealed class HttpMcpTransport : IMcpTransport
{
    private const int DiagnosticTruncateAt = 200;

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Uri _endpoint;
    private readonly Channel<string> _incoming;
    private readonly int _maxFrameBytes;
    private readonly int _maxResponseBytes;
    private readonly TimeSpan _idleTimeout;
    private string? _lastDiagnostic;
    private bool _disposed;

    private HttpMcpTransport(HttpClient httpClient, bool ownsHttpClient, Uri endpoint, McpConfig config)
    {
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        _endpoint = endpoint;
        _maxFrameBytes = config.MaxFrameBytes;
        _maxResponseBytes = config.MaxResponseBytes;
        _idleTimeout = TimeSpan.FromSeconds(config.IdleTimeoutSeconds);
        _incoming = Channel.CreateBounded<string>(new BoundedChannelOptions(config.MaxQueuedFrames)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
    }

    public bool HasExited => _disposed;

    public int? ExitCode => null;

    public static Task<IMcpTransport> ConnectAsync(McpConfig config, CancellationToken ct)
    {
        var uri = HttpMcpUrlValidator.Validate(config);
        var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(config.RequestTimeoutSeconds) };
        return Task.FromResult<IMcpTransport>(new HttpMcpTransport(httpClient, ownsHttpClient: true, uri, config));
    }

    // Test seam: callers own the HttpClient lifetime and provide a handler
    // (typically StubHandler) for hermetic unit tests.
    internal static IMcpTransport CreateForTesting(HttpClient httpClient, McpConfig config)
    {
        var uri = HttpMcpUrlValidator.Validate(config);
        return new HttpMcpTransport(httpClient, ownsHttpClient: false, uri, config);
    }

    public async Task SendAsync(string json, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // HttpRequestException + ObjectDisposedException + TaskCanceledException
        // are wrapped as IOException so McpToolConnector.InvokeAsync's catch
        // filter handles them as transport failures (and so we control what
        // diagnostic text reaches logs — no raw server bodies).
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            _lastDiagnostic = Truncate(ex.Message);
            throw new IOException($"HTTP transport send failed: {Truncate(ex.Message)}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _lastDiagnostic = "request timed out";
            throw new IOException("HTTP transport send timed out", ex);
        }
        catch (ObjectDisposedException ex)
        {
            throw new IOException("HTTP transport disposed during send", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NoContent)
                return;

            if (!response.IsSuccessStatusCode)
            {
                _lastDiagnostic = $"HTTP {(int)response.StatusCode}";
                throw new IOException(_lastDiagnostic);
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                await ConsumeJsonAsync(response, ct);
                return;
            }
            if (string.Equals(contentType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                await ConsumeEventStreamAsync(response, ct);
                return;
            }

            _lastDiagnostic = $"unexpected Content-Type '{Truncate(contentType ?? "(none)")}'";
            throw new IOException(_lastDiagnostic);
        }
    }

    private async Task ConsumeJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is long declared && declared > _maxResponseBytes)
        {
            _lastDiagnostic = $"response Content-Length {declared} exceeds limit {_maxResponseBytes}";
            throw new IOException(_lastDiagnostic);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        using var sink = new MemoryStream();
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                if (sink.Length + read > _maxResponseBytes)
                {
                    _lastDiagnostic = $"response body exceeded limit {_maxResponseBytes}";
                    throw new IOException(_lastDiagnostic);
                }
                sink.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (sink.Length > _maxFrameBytes)
        {
            _lastDiagnostic = $"JSON response body {sink.Length} exceeds frame limit {_maxFrameBytes}";
            throw new IOException(_lastDiagnostic);
        }

        var body = Encoding.UTF8.GetString(sink.GetBuffer(), 0, (int)sink.Length);
        await _incoming.Writer.WriteAsync(body, ct);
    }

    private async Task ConsumeEventStreamAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var dataBuf = new StringBuilder();
        long totalBytes = 0;

        while (true)
        {
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idleCts.CancelAfter(_idleTimeout);

            string? line;
            try
            {
                line = await reader.ReadLineAsync(idleCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _lastDiagnostic = $"SSE idle timeout ({_idleTimeout.TotalSeconds:0}s)";
                throw new IOException(_lastDiagnostic);
            }

            if (line is null) break;

            totalBytes += line.Length;
            if (totalBytes > _maxResponseBytes)
            {
                _lastDiagnostic = $"SSE stream exceeded total-bytes limit {_maxResponseBytes}";
                throw new IOException(_lastDiagnostic);
            }

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
                var payload = line.Length > 5 && line[5] == ' ' ? line.AsSpan(6) : line.AsSpan(5);
                if (dataBuf.Length + payload.Length > _maxFrameBytes)
                {
                    _lastDiagnostic = $"SSE data frame exceeded limit {_maxFrameBytes}";
                    throw new IOException(_lastDiagnostic);
                }
                if (dataBuf.Length > 0) dataBuf.Append('\n');
                dataBuf.Append(payload);
            }
        }

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
        if (_ownsHttpClient)
            _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string Truncate(string s) =>
        s.Length <= DiagnosticTruncateAt ? s : s[..DiagnosticTruncateAt] + "...";
}
