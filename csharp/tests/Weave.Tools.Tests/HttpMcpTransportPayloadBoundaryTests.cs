using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Weave.Tools.Connectors;
using Weave.Workspaces.Manifest;

namespace Weave.Tools.Tests;

public sealed class HttpMcpTransportPayloadBoundaryTests
{
    [Theory]
    [InlineData("data: 汉汉\n\n", 5, 64)]
    [InlineData(": 汉汉\n\n", 16, 8)]
    [InlineData("\r\n\r\n\r\n\r\n\r\n", 16, 8)]
    [InlineData("data: aa\ndata: bb\n\n", 4, 64)]
    public async Task SendAsync_SseByteBudget_CountsUtf8DelimitersAndJoinedNewlines(string text, int frameLimit, int responseLimit)
    {
        using var body = new ObservedBody(Encoding.UTF8.GetBytes(text), 1);
        using var client = Client(body, "text/event-stream");
        await using var transport = HttpMcpTransport.CreateForTesting(client, Config(frameLimit, responseLimit));

        await Should.ThrowAsync<IOException>(() => transport.SendAsync("{}", TestContext.Current.CancellationToken));

        body.WasDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task SendAsync_UnterminatedOversizedLine_StopsAtByteBudgetBeforeAllocatingWholeLine()
    {
        using var body = new ObservedBody(Encoding.UTF8.GetBytes("data: " + new string('x', 8192)));
        using var client = Client(body, "text/event-stream");
        await using var transport = HttpMcpTransport.CreateForTesting(client, Config(16, 64));

        await Should.ThrowAsync<IOException>(() => transport.SendAsync("{}", TestContext.Current.CancellationToken));

        body.BytesRead.ShouldBeLessThanOrEqualTo(65);
        body.WasDisposed.ShouldBeTrue();
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public async Task SendAsync_ExactUtf8LimitsAndSplitCharacters_ReturnsUnchangedFrame(string newline)
    {
        var text = "\uFEFFdata: 汉" + newline + "data: 🙂" + newline + newline;
        using var body = new ObservedBody(Encoding.UTF8.GetBytes(text), 1);
        using var client = Client(body, "text/event-stream");
        await using var transport = HttpMcpTransport.CreateForTesting(client, Config(8, Encoding.UTF8.GetByteCount(text)));

        await transport.SendAsync("{}", TestContext.Current.CancellationToken);

        (await transport.ReceiveAsync(TestContext.Current.CancellationToken)).ShouldBe("汉\n🙂");
        body.WasDisposed.ShouldBeTrue();
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/event-stream")]
    public async Task SendAsync_InvalidUtf8_RejectsInsteadOfReplacingWireInput(string contentType)
    {
        var prefix = contentType == "text/event-stream" ? "data: \"" : "\"";
        var suffix = contentType == "text/event-stream" ? "\"\n\n" : "\"";
        byte[] bytes = [.. Encoding.UTF8.GetBytes(prefix), 0xC3, 0x28, .. Encoding.UTF8.GetBytes(suffix)];
        using var body = new ObservedBody(bytes, 1);
        using var client = Client(body, contentType);
        await using var transport = HttpMcpTransport.CreateForTesting(client, Config(64, 128));

        await Should.ThrowAsync<IOException>(() => transport.SendAsync("{}", TestContext.Current.CancellationToken));

        body.WasDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task SendAsync_SseMissingFinalBlankLine_DoesNotPromotePartialEventToCompleteFrame()
    {
        using var body = new ObservedBody(Encoding.UTF8.GetBytes("data: {\"result\":true}\n"));
        using var client = Client(body, "text/event-stream");
        await using var transport = HttpMcpTransport.CreateForTesting(client, Config(64, 128));

        await Should.ThrowAsync<IOException>(() => transport.SendAsync("{}", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SendAsync_SseLeadingEmptyDataField_PreservesJoiningNewline()
    {
        using var body = new ObservedBody(Encoding.UTF8.GetBytes("data:\ndata: x\n\n"));
        using var client = Client(body, "text/event-stream");
        await using var transport = HttpMcpTransport.CreateForTesting(client, Config(2, 64));

        await transport.SendAsync("{}", TestContext.Current.CancellationToken);

        (await transport.ReceiveAsync(TestContext.Current.CancellationToken)).ShouldBe("\nx");
    }

    [Fact]
    public async Task SendAsync_UnexpectedContentType_DoesNotReflectArbitraryPeerHeader()
    {
        using var body = new ObservedBody([]);
        using var client = Client(body, "application/x-synthetic-private-value");
        await using var transport = HttpMcpTransport.CreateForTesting(client, Config(64, 128));

        var error = await Should.ThrowAsync<IOException>(() => transport.SendAsync("{}", TestContext.Current.CancellationToken));

        error.Message.ShouldNotContain("synthetic-private-value");
        transport.FormatDiagnosticTail().ShouldNotContain("synthetic-private-value");
    }

    private static McpConfig Config(int frameLimit, int responseLimit) => new()
    {
        Url = "https://example.test/mcp",
        MaxFrameBytes = frameLimit,
        MaxResponseBytes = responseLimit,
        RequestTimeoutSeconds = 2
    };

    private static HttpClient Client(Stream body, string contentType) => new(new ResponseHandler(body, contentType));

    private sealed class ResponseHandler(Stream body, string contentType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            return Task.FromResult(response);
        }
    }

    private sealed class ObservedBody(byte[] bytes, int chunkSize = int.MaxValue) : Stream
    {
        public int BytesRead { get; private set; }
        public bool WasDisposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var length = Math.Min(Math.Min(count, chunkSize), bytes.Length - BytesRead);
            bytes.AsSpan(BytesRead, length).CopyTo(buffer.AsSpan(offset, length));
            BytesRead += length;
            return length;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Math.Min(Math.Min(buffer.Length, chunkSize), bytes.Length - BytesRead);
            bytes.AsMemory(BytesRead, length).CopyTo(buffer);
            BytesRead += length;
            return ValueTask.FromResult(length);
        }
        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
