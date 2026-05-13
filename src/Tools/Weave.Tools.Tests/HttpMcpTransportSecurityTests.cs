using System.Net;
using System.Text;
using Weave.Tools.Connectors;
using Weave.Workspaces.Manifest;
namespace Weave.Tools.Tests;

// Adversarial-peer + adversarial-config tests. Each test pins a specific
// defense in HttpMcpTransport. We use a private constructor seam
// (CreateForTesting) so the transport talks to a StubHandler instead of
// a real socket — gives us hermetic control over headers, status codes,
// body size, and inter-frame timing.
public sealed class HttpMcpTransportSecurityTests
{
    // ---- SSRF: URL validation ----

    [Theory]
    [InlineData("http://127.0.0.1/mcp", "loopback")]
    [InlineData("http://localhost/mcp", "loopback")]
    [InlineData("http://[::1]/mcp", "loopback")]
    [InlineData("http://10.0.0.1/mcp", "private or reserved")]
    [InlineData("http://172.16.0.1/mcp", "private or reserved")]
    [InlineData("http://172.31.255.255/mcp", "private or reserved")]
    [InlineData("http://192.168.1.1/mcp", "private or reserved")]
    [InlineData("http://169.254.169.254/mcp", "private or reserved")] // AWS/Azure IMDS — the canonical SSRF target
    [InlineData("http://100.64.0.1/mcp", "private or reserved")]      // CGNAT
    [InlineData("http://0.0.0.0/mcp", "private or reserved")]
    [InlineData("http://[fe80::1]/mcp", "private or reserved")]
    [InlineData("http://[fc00::1]/mcp", "private or reserved")]
    public void ConnectAsync_PrivateOrLoopbackUrl_Rejected_ByDefault(string url, string expectedReason)
    {
        var config = new McpConfig { Url = url };
        var ex = Should.Throw<InvalidOperationException>(
            () => HttpMcpTransport.ConnectAsync(config, CancellationToken.None));
        ex.Message.ShouldContain(expectedReason);
    }

    [Fact]
    public void ConnectAsync_LoopbackUrl_AcceptedWithExplicitOptIn()
    {
        var config = new McpConfig { Url = "http://127.0.0.1:1/mcp", AllowPrivateEndpoints = true };
        // The factory only validates the URL — it returns a transport without contacting the network.
        var task = HttpMcpTransport.ConnectAsync(config, CancellationToken.None);
        task.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public void ConnectAsync_UrlWithUserinfo_AlwaysRejected()
    {
        var config = new McpConfig
        {
            Url = "http://user:password@example.com/mcp",
            AllowPrivateEndpoints = true // even with opt-in, userinfo is still refused
        };
        var ex = Should.Throw<InvalidOperationException>(
            () => HttpMcpTransport.ConnectAsync(config, CancellationToken.None));
        ex.Message.ShouldContain("userinfo");
    }

    [Fact]
    public void ConnectAsync_UrlWithFragment_AlwaysRejected()
    {
        var config = new McpConfig
        {
            Url = "https://example.com/mcp#secret",
            AllowPrivateEndpoints = true
        };
        var ex = Should.Throw<InvalidOperationException>(
            () => HttpMcpTransport.ConnectAsync(config, CancellationToken.None));
        ex.Message.ShouldContain("fragment");
    }

    [Theory]
    [InlineData("ftp://example.com/mcp")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not-a-url")]
    public void ConnectAsync_NonHttpScheme_Rejected(string url)
    {
        var config = new McpConfig { Url = url };
        Should.Throw<InvalidOperationException>(() => HttpMcpTransport.ConnectAsync(config, CancellationToken.None));
    }

    // ---- Resource bounds (memory DoS defense) ----

    [Fact]
    public async Task SendAsync_JsonContentLengthExceedingLimit_Throws_BeforeReadingBody()
    {
        // Server advertises Content-Length above the cap — defense fires from the
        // header check, before a single body byte is read. Pins the cheap-path
        // defense against honest-but-too-big servers.
        var handler = new StubHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[2048])
            };
            resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            resp.Content.Headers.ContentLength = 2048;
            return resp;
        });
        var transport = HttpMcpTransport.CreateForTesting(new HttpClient(handler),
            new McpConfig { Url = "https://example.test/mcp", MaxResponseBytes = 1024 });

        var ex = await Should.ThrowAsync<IOException>(
            () => transport.SendAsync("{}", TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("Content-Length");
    }

    [Fact]
    public async Task SendAsync_JsonBodyExceedingMaxResponseBytes_NoContentLength_Throws()
    {
        // Server hides Content-Length (or lies low) — defense must still fire
        // mid-stream from the running byte cap. Pins the streaming-path defense
        // against dishonest servers that under-report.
        var handler = new StubHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new UnsizedStream(Encoding.UTF8.GetBytes(new string('x', 4096))))
            };
            resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return resp;
        });
        var transport = HttpMcpTransport.CreateForTesting(new HttpClient(handler),
            new McpConfig { Url = "https://example.test/mcp", MaxResponseBytes = 1024 });

        var ex = await Should.ThrowAsync<IOException>(
            () => transport.SendAsync("{}", TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("body exceeded");
    }

    [Fact]
    public async Task SendAsync_SseDataFrameExceedingMaxFrameBytes_Throws()
    {
        // One enormous data: line, no terminator yet — must fail mid-stream before OOMing.
        var bigPayload = new string('A', 4096);
        var sse = $"data: {bigPayload}\n\n";
        var handler = new StubHandler(_ => Sse(sse));
        var transport = HttpMcpTransport.CreateForTesting(new HttpClient(handler),
            new McpConfig { Url = "https://example.test/mcp", MaxFrameBytes = 1024, MaxResponseBytes = 1_000_000 });

        var ex = await Should.ThrowAsync<IOException>(
            () => transport.SendAsync("{}", TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("SSE data frame exceeded");
    }

    [Fact]
    public async Task SendAsync_SseTotalStreamExceedingMaxResponseBytes_Throws()
    {
        // Many small data: frames that individually fit MaxFrameBytes but cumulatively blow MaxResponseBytes.
        var sb = new StringBuilder();
        for (var i = 0; i < 1000; i++)
            sb.Append("data: x\n\n");
        var handler = new StubHandler(_ => Sse(sb.ToString()));
        var transport = HttpMcpTransport.CreateForTesting(new HttpClient(handler),
            new McpConfig
            {
                Url = "https://example.test/mcp",
                MaxFrameBytes = 1024,
                MaxResponseBytes = 1024 // tight cap so cumulative bytes trip it
            });

        var ex = await Should.ThrowAsync<IOException>(
            () => transport.SendAsync("{}", TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("total-bytes");
    }

    // ---- Content-type defense ----

    [Fact]
    public async Task SendAsync_UnexpectedContentType_Throws()
    {
        var handler = new StubHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("plaintext", Encoding.UTF8, "text/plain")
            };
            return resp;
        });
        var transport = HttpMcpTransport.CreateForTesting(new HttpClient(handler),
            new McpConfig { Url = "https://example.test/mcp" });

        var ex = await Should.ThrowAsync<IOException>(
            () => transport.SendAsync("{}", TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("Content-Type");
    }

    [Fact]
    public async Task SendAsync_5xxStatus_Throws_WithoutLeakingBody()
    {
        var secret = "internal-stack-trace-with-/etc/secrets";
        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(secret, Encoding.UTF8, "application/json"),
                ReasonPhrase = "Internal Server Error"
            });
        var transport = HttpMcpTransport.CreateForTesting(new HttpClient(handler),
            new McpConfig { Url = "https://example.test/mcp" });

        var ex = await Should.ThrowAsync<IOException>(
            () => transport.SendAsync("{}", TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("HTTP 500"); // pins we actually took the 5xx path
        ex.Message.ShouldNotContain(secret); // server body must never reach the exception text
        transport.FormatDiagnosticTail().ShouldNotContain(secret);
        await transport.DisposeAsync();
    }

    // ---- Idle timeout (slow-loris) ----

    [Fact]
    public async Task SendAsync_SseStreamIdle_TimesOut()
    {
        // Server opens an SSE stream and dribbles forever (one byte, sleeps past idle timeout).
        var handler = new StubHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            resp.Content = new StreamContent(new SlowStream(idleMs: 5000));
            resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return resp;
        });
        var transport = HttpMcpTransport.CreateForTesting(new HttpClient(handler),
            new McpConfig { Url = "https://example.test/mcp", IdleTimeoutSeconds = 1, RequestTimeoutSeconds = 30 });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Should.ThrowAsync<IOException>(
            () => transport.SendAsync("{}", TestContext.Current.CancellationToken));
        sw.Stop();
        ex.Message.ShouldContain("idle timeout");
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3)); // fired close to 1s, not the 5s the stream wants
    }

    // ---- helpers ----

    private static HttpResponseMessage Sse(string body)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
        };
        return resp;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    // Hides Length/CanSeek so HttpClient cannot pre-compute Content-Length,
    // forcing the transport down the streaming-read path instead of the
    // header-check shortcut.
    private sealed class UnsizedStream(byte[] data) : Stream
    {
        private int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(count, data.Length - _pos);
            if (n <= 0)
                return 0;
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // A stream that sleeps between byte yields so it triggers SSE idle timeout.
    private sealed class SlowStream(int idleMs) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(idleMs, cancellationToken);
            buffer.Span[0] = (byte)'.';
            return 1;
        }
    }
}
