using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Weave.Cli.Commands.Local;

namespace Weave.Cli.Tests;

public sealed class LocalReviewResponseBoundsTests
{
    private const string Id = "82c07b3d2a3646e88f2f0b8db07c452b";
    // Host: 1 MiB request, 1,024-character subject, 8,192-character target;
    // up to six JSON bytes per character, plus bounded identifiers and envelope.
    private const int ReviewResponseLimit = 6 * (1_048_576 + 1024 + 8192) + 1024;
    private static readonly string Digest = "approval-v1:" + new string('A', 64);
    private static readonly JsonSerializerOptions Unescaped = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Theory]
    [InlineData('x', true)]
    [InlineData('x', false)]
    [InlineData('<', true)]
    [InlineData('<', false)]
    public async Task RunAsync_HostValidNearLimitProposal_DisplaysCompleteReview(char character, bool contentLength)
    {
        var proposal = new JsonObject
        {
            ["invocationId"] = Id,
            ["toolName"] = "files",
            ["method"] = "write_file",
            ["parameters"] = new JsonObject { ["path"] = "summary.md" },
            ["rawInput"] = ""
        };
        var envelopeBytes = Encoding.UTF8.GetByteCount(proposal.ToJsonString(Unescaped));
        var content = new string(character, 1_048_576 - envelopeBytes);
        proposal["rawInput"] = content;
        Encoding.UTF8.GetByteCount(proposal.ToJsonString(Unescaped)).ShouldBe(1_048_576);
        var payload = Encoding.UTF8.GetBytes(Preview(content).ToJsonString());
        payload.Length.ShouldBeGreaterThan(LocalHttp.MaxBytes);
        payload.Length.ShouldBeLessThanOrEqualTo(ReviewResponseLimit);
        using var stream = new ResponseStream(payload);
        using var handler = ReviewHandler(stream, contentLength ? payload.Length : null);
        using var client = Client(handler);
        var terminal = new ReviewTerminal();

        var result = await ReviewAsync(new LocalHttp(client, TimeProvider.System), terminal);

        result.ShouldBe(LocalReviewOutcome.Unchanged);
        terminal.Output.ShouldContain(content);
        terminal.Output.ShouldContain("subject: " + new string('<', 1024));
        terminal.Output.ShouldContain("targetDescription: " + new string('<', 8192));
        terminal.Output.ShouldContain("Type 'approve " + Digest + "' or 'reject " + Digest + "'. Any other input leaves the request unchanged.");
        stream.BytesRead.ShouldBe(payload.Length);
        handler.Requests.Any(request => request.Path.EndsWith("/decision", StringComparison.Ordinal)
            || request.Path.EndsWith("/resume", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_ReviewExceedsDerivedBound_RejectsBeforeDisplayingOrDeciding(bool contentLength)
    {
        using var stream = new ResponseStream(Encoding.UTF8.GetBytes(new string('x', ReviewResponseLimit + 1)));
        using var handler = ReviewHandler(stream, contentLength ? ReviewResponseLimit + 1 : null);
        using var client = Client(handler);
        var terminal = new ReviewTerminal();

        var error = await Should.ThrowAsync<HttpRequestException>(() => ReviewAsync(new LocalHttp(client, TimeProvider.System), terminal));

        error.Message.ShouldBe("Local API response exceeds the client limit.");
        terminal.Output.ShouldBeEmpty();
        if (contentLength)
            stream.BytesRead.ShouldBe(0);
        else
        {
            stream.BytesRead.ShouldBeGreaterThan(0);
            stream.BytesRead.ShouldBeLessThanOrEqualTo(ReviewResponseLimit + 1);
        }
        handler.Requests.Any(request => request.Path.EndsWith("/decision", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallAsync_OrdinaryResponseExceedsOneMiB_RetainsOriginalBound(bool contentLength)
    {
        using var stream = new ResponseStream(Encoding.UTF8.GetBytes(new string('x', LocalHttp.MaxBytes + 1)));
        using var handler = new LocalHttpFixture((_, _) => Response(stream, contentLength ? LocalHttp.MaxBytes + 1 : null));
        using var client = Client(handler);

        await Should.ThrowAsync<HttpRequestException>(() => new LocalHttp(client, TimeProvider.System)
            .CallAsync(HttpMethod.Get, "/api/workspaces/onboarding/tools/files/invocations/" + Id,
                "agent", null, null, TestContext.Current.CancellationToken));

        stream.BytesRead.ShouldBe(contentLength ? 0 : LocalHttp.MaxBytes + 1);
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task CallAsync_RequestExceedsOneMiB_RejectsWithoutSending()
    {
        using var handler = new LocalHttpFixture((_, _) => LocalHttpFixture.Response(202));
        using var client = Client(handler);
        var body = new JsonObject { ["rawInput"] = new string('x', LocalHttp.MaxBytes) };

        await Should.ThrowAsync<ArgumentException>(() => new LocalHttp(client, TimeProvider.System)
            .CallAsync(HttpMethod.Post, "/api/workspaces/onboarding/tools/files/invocations",
                "agent", null, body, TestContext.Current.CancellationToken));

        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task RunAsync_ReviewBodyStalls_DeadlineStillCancelsRead()
    {
        using var stream = new CancellationBoundStream();
        using var handler = ReviewHandler(stream, null);
        using var client = Client(handler);
        var clock = new FakeTimeProvider();
        var terminal = new ReviewTerminal();
        var pending = ReviewAsync(new LocalHttp(client, clock), terminal);
        await stream.Reading.Task.WaitAsync(TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(15));
        await Should.ThrowAsync<OperationCanceledException>(() => pending);

        terminal.Output.ShouldBeEmpty();
        handler.Requests.Any(request => request.Path.EndsWith("/decision", StringComparison.Ordinal)).ShouldBeFalse();
    }

    private static Task<LocalReviewOutcome> ReviewAsync(LocalHttp http, ReviewTerminal terminal) =>
        new LocalReview(terminal).RunAsync(http, "onboarding", Id, "reviewer", "operator", TestContext.Current.CancellationToken);

    private static JsonObject Preview(string content) => new()
    {
        ["invocationId"] = Id,
        ["workspaceId"] = "onboarding",
        ["subject"] = new string('<', 1024),
        ["toolName"] = "files",
        ["operation"] = "write_file",
        ["targetDescription"] = new string('<', 8192),
        ["parameters"] = new JsonObject { ["path"] = "summary.md" },
        ["rawInput"] = content,
        ["planDigest"] = Digest,
        ["expiresAt"] = "2030-01-01T00:00:00Z"
    };

    private static LocalHttpFixture ReviewHandler(Stream stream, int? contentLength) => new((request, _) =>
        request.RequestUri!.AbsolutePath.EndsWith("/connect", StringComparison.Ordinal)
            ? LocalHttpFixture.Response(204) : Response(stream, contentLength));

    private static HttpResponseMessage Response(Stream stream, int? contentLength)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        if (contentLength is not null)
            response.Content.Headers.ContentLength = contentLength;
        return response;
    }

    private static HttpClient Client(LocalHttpFixture handler) => new(handler)
    {
        BaseAddress = new Uri("http://127.0.0.1:9401")
    };

    private sealed class ReviewTerminal : ILocalReviewConsole
    {
        public bool IsInteractive => true;
        public List<string> Output { get; } = [];
        public void WriteLine(string text) => Output.Add(text);
        public ValueTask<string?> ReadLineAsync(CancellationToken ct) => ValueTask.FromResult<string?>("");
    }

    private sealed class ResponseStream(byte[] bytes) : Stream
    {
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var count = Math.Min(buffer.Length, bytes.Length - BytesRead);
            bytes.AsSpan(BytesRead, count).CopyTo(buffer);
            BytesRead += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
    }
}
