using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Weave.Workspaces.Manifest;

namespace Weave.Tools.Connectors;

// Streamable HTTP MCP: one configured endpoint, bounded response data and a
// deadline covering headers, body consumption and queue backpressure. The
// existing URL policy still requires deployment egress controls for DNS names.
// No automatic redirects, cookies or application-level retries. GET streams
// and Authorization pass-through remain out of scope.
internal sealed partial class HttpMcpTransport : IMcpTransport
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Uri _endpoint;
    private readonly Channel<string> _incoming;
    private readonly int _maxFrameBytes;
    private readonly int _maxResponseBytes;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _requestTimeout;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private string? _lastDiagnostic;
    private string? _sessionId;
    private bool _sessionExpired;
    private int _disposed;

    private HttpMcpTransport(HttpClient httpClient, bool ownsHttpClient, Uri endpoint, McpConfig config)
    {
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        _endpoint = endpoint;
        _maxFrameBytes = config.MaxFrameBytes;
        _maxResponseBytes = config.MaxResponseBytes;
        _idleTimeout = TimeSpan.FromSeconds(config.IdleTimeoutSeconds);
        _requestTimeout = TimeSpan.FromSeconds(config.RequestTimeoutSeconds);
        _shutdownToken = _shutdown.Token;
        _incoming = Channel.CreateBounded<string>(new BoundedChannelOptions(config.MaxQueuedFrames)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
    }

    public bool HasExited => Volatile.Read(ref _disposed) != 0;
    public bool SupportsModernProtocol => true;
    public bool UsesHttpHeaders => true;
    public TimeSpan? ModernProbeTimeout => null;

    public int? ExitCode => null;

    public static Task<IMcpTransport> ConnectAsync(McpConfig config, CancellationToken ct)
    {
        var uri = HttpMcpUrlValidator.Validate(config);
        var httpClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        })
        {
            Timeout = TimeSpan.FromSeconds(config.RequestTimeoutSeconds)
        };
        return Task.FromResult<IMcpTransport>(new HttpMcpTransport(httpClient, ownsHttpClient: true, uri, config));
    }

    // Borrowed clients are a test seam; their handlers are controlled by tests.
    internal static IMcpTransport CreateForTesting(HttpClient httpClient, McpConfig config)
    {
        var uri = HttpMcpUrlValidator.Validate(config);
        return new HttpMcpTransport(httpClient, ownsHttpClient: false, uri, config);
    }

    public async Task SendAsync(string json, CancellationToken ct, McpRequestMetadata? metadata = null)
    {
        ObjectDisposedException.ThrowIf(HasExited, this);
        if (_sessionExpired)
            throw new IOException("MCP HTTP session expired; a new connection is required.");
        _lastDiagnostic = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdownToken);
        deadline.CancelAfter(_requestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            if (metadata is not null)
            {
                request.Headers.Add("MCP-Protocol-Version", metadata.ProtocolVersion);
                if (metadata.IncludeRoutingHeaders)
                {
                    request.Headers.Add("Mcp-Method", metadata.Method);
                    if (metadata.Name is not null)
                        request.Headers.Add("Mcp-Name", McpHttpHeaderValue.Encode(metadata.Name));
                    if (metadata.ParameterHeaders is not null)
                        foreach (var (name, value) in metadata.ParameterHeaders)
                            request.Headers.Add($"Mcp-Param-{name}", McpHttpHeaderValue.Encode(value));
                }
            }
            if (_sessionId is not null)
                request.Headers.Add("Mcp-Session-Id", _sessionId);

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.NoContent)
                return;
            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                using var message = JsonDocument.Parse(json);
                if (message.RootElement.TryGetProperty("id", out _))
                    throw Failure("HTTP 202 cannot acknowledge an MCP request");
                return;
            }
            if (!response.IsSuccessStatusCode)
            {
                if (_sessionId is not null && response.StatusCode == HttpStatusCode.NotFound)
                    _sessionExpired = true;
                if (metadata is not null && response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
                    throw new McpHttpStatusException(response.StatusCode,
                        await ReadProtocolErrorCodeAsync(response, deadline.Token));
                throw Failure($"HTTP {(int)response.StatusCode}");
            }

            using (var sent = JsonDocument.Parse(json))
                if (sent.RootElement.TryGetProperty("method", out var sentMethod)
                    && sentMethod.GetString() == "initialize"
                    && response.Headers.TryGetValues("Mcp-Session-Id", out var sessionValues))
                {
                    var values = sessionValues.ToArray();
                    if (values.Length != 1 || values[0].Length is < 1 or > 1024
                        || values[0].Any(static character => character is < '!' or > '~'))
                        throw Failure("invalid MCP HTTP session identifier");
                    _sessionId = values[0];
                }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase))
                await ConsumeJsonAsync(response, deadline.Token);
            else if (string.Equals(contentType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                await ConsumeEventStreamAsync(response, deadline.Token);
            else
                throw Failure("unexpected HTTP Content-Type");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw Failure(HasExited ? "HTTP transport disposed during send" : "HTTP transport send timed out", ex);
        }
        catch (HttpRequestException ex)
        {
            throw Failure("HTTP transport send failed", ex);
        }
        catch (ObjectDisposedException ex)
        {
            throw Failure("HTTP transport disposed during send", ex);
        }
        catch (ChannelClosedException ex)
        {
            throw Failure("HTTP transport response queue closed", ex);
        }
        catch (DecoderFallbackException ex)
        {
            throw Failure("HTTP response contains invalid UTF-8", ex);
        }
        catch (IOException ex) when (ex is not McpHttpStatusException && _lastDiagnostic is null)
        {
            throw Failure("HTTP response read failed", ex);
        }
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        try
        { return await _incoming.Reader.ReadAsync(ct); }
        catch (ChannelClosedException) { return null; }
    }

    public string FormatDiagnosticTail() =>
        _lastDiagnostic is null ? string.Empty : $"last HTTP error: {_lastDiagnostic}";

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            await _shutdown.CancelAsync();
        }
        finally
        {
            _incoming.Writer.TryComplete();
            if (_ownsHttpClient)
                _httpClient.Dispose();
            _shutdown.Dispose();
        }
    }

    private IOException Failure(string message, Exception? cause = null)
    {
        _lastDiagnostic = message;
        return new IOException(message, cause);
    }
}
