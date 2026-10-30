using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Weave.Silo.Tests.Invocations;

namespace Weave.Silo.Tests.Dashboard;

public sealed class DashboardReviewBoundaryTests
{
    private const int ResponseLimit = 8_388_608;
    private const string Input = """
        {"invocationId":"abcdef0123456789abcdef0123456789","toolName":"files","method":"write_file","parameters":{"path":"note.txt"},"rawInput":"reviewed text"}
        """;

    [Theory]
    [InlineData("text/html", null)]
    [InlineData("application/json", ResponseLimit + 1L)]
    public async Task LoadAsync_UntrustedResponseHeaders_RejectsWithoutReadingBody(string mediaType, long? length)
    {
        using var stream = new DashboardReviewResponseStream(32);
        using var http = DashboardPageFixture.Http((_, _) => Task.FromResult(Reply(stream, mediaType, length)));
        dynamic session = DashboardReviewSessionTests.Session(http, TimeProvider.System);
        using var lifetime = (IDisposable)session;

        await session.LoadAsync("workspace", "files", Input, "test-capability", "", TestContext.Current.CancellationToken);

        ((object?)session.Snapshot).ShouldBeNull();
        ((string?)session.Error).ShouldBe("The review response is invalid or too large.");
        ((bool)session.Loading).ShouldBeFalse();
        stream.BytesRead.ShouldBe(0);
        stream.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task LoadAsync_ChunkedResponseExceedsBound_StopsReadingAndDiscardsSnapshot()
    {
        using var stream = new DashboardReviewResponseStream(ResponseLimit + 100L);
        using var http = DashboardPageFixture.Http((_, _) => Task.FromResult(Reply(stream, "application/json", null)));
        dynamic session = DashboardReviewSessionTests.Session(http, TimeProvider.System);
        using var lifetime = (IDisposable)session;

        await session.LoadAsync("workspace", "files", Input, "test-capability", "", TestContext.Current.CancellationToken);

        ((object?)session.Snapshot).ShouldBeNull();
        ((string?)session.Error).ShouldBe("The review response is invalid or too large.");
        ((bool)session.Loading).ShouldBeFalse();
        stream.BytesRead.ShouldBe(ResponseLimit + 1L);
        stream.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task LoadAsync_ResponseStreamFails_ReportsSafeErrorAndDisposesStream()
    {
        using var stream = new DashboardReviewResponseStream(32, failRead: true);
        using var http = DashboardPageFixture.Http((_, _) => Task.FromResult(Reply(stream, "application/json", null)));
        dynamic session = DashboardReviewSessionTests.Session(http, TimeProvider.System);
        using var lifetime = (IDisposable)session;

        await session.LoadAsync("workspace", "files", Input, "test-capability", "", TestContext.Current.CancellationToken);

        ((object?)session.Snapshot).ShouldBeNull();
        ((string?)session.Error).ShouldBe("Unable to obtain a verified review. Check the configured server and verify again.");
        ((bool)session.Loading).ShouldBeFalse();
        stream.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task LoadAsync_InvalidResponseJson_ReportsSafeErrorWithoutUpstreamContent()
    {
        using var http = DashboardPageFixture.Http((_, _) =>
            Task.FromResult(DashboardPageFixture.Json("{private-response-marker")));
        dynamic session = DashboardReviewSessionTests.Session(http, TimeProvider.System);
        using var lifetime = (IDisposable)session;

        await session.LoadAsync("workspace", "files", Input, "test-capability", "", TestContext.Current.CancellationToken);

        ((object?)session.Snapshot).ShouldBeNull();
        ((string?)session.Error).ShouldBe("Unable to obtain a verified review. Check the configured server and verify again.");
        ((bool)session.Loading).ShouldBeFalse();
    }

    [Fact]
    public async Task LoadAsync_MissingReview_ExplainsUnavailableApproval()
    {
        using var http = DashboardPageFixture.Http((_, _) =>
            Task.FromResult(DashboardPageFixture.Json("private-response-marker", HttpStatusCode.NotFound)));
        dynamic session = DashboardReviewSessionTests.Session(http, TimeProvider.System);
        using var lifetime = (IDisposable)session;

        await session.LoadAsync("workspace", "files", Input, "test-capability", "", TestContext.Current.CancellationToken);

        ((object?)session.Snapshot).ShouldBeNull();
        ((string?)session.Error).ShouldBe("The pending approval or review route is unavailable.");
        ((bool)session.Loading).ShouldBeFalse();
    }

    [Theory]
    [InlineData("array")]
    [InlineData("unknown-field")]
    [InlineData("duplicate-field")]
    [InlineData("number-parameter")]
    [InlineData("duplicate-parameter")]
    [InlineData("object-raw-input")]
    public async Task LoadAsync_AmbiguousOriginalInput_RejectsBeforeSending(string defect)
    {
        var input = defect switch
        {
            "array" => "[" + Input + "]",
            "unknown-field" => Input.Replace("{\"invocationId\"", "{\"secret\":\"private-input-marker\",\"invocationId\"", StringComparison.Ordinal),
            "duplicate-field" => Input.Replace("{\"invocationId\"", "{\"toolName\":\"files\",\"invocationId\"", StringComparison.Ordinal),
            "number-parameter" => Input.Replace("\"path\":\"note.txt\"", "\"path\":42", StringComparison.Ordinal),
            "duplicate-parameter" => Input.Replace("\"path\":\"note.txt\"", "\"path\":\"note.txt\",\"path\":\"other.txt\"", StringComparison.Ordinal),
            "object-raw-input" => Input.Replace("\"rawInput\":\"reviewed text\"", "\"rawInput\":{}", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(defect))
        };
        var sent = false;
        using var http = DashboardPageFixture.Http((_, _) =>
        {
            sent = true;
            return Task.FromResult(DashboardPageFixture.Json("null"));
        });
        dynamic session = DashboardReviewSessionTests.Session(http, TimeProvider.System);
        using var lifetime = (IDisposable)session;

        await session.LoadAsync("workspace", "files", input, "test-capability", "", TestContext.Current.CancellationToken);

        sent.ShouldBeFalse();
        ((object?)session.Snapshot).ShouldBeNull();
        ((string?)session.Error).ShouldBe("Use the complete original invocation JSON, matching tool and a nonempty 32-hex invocation ID (maximum 1 MiB).");
        ((bool)session.Loading).ShouldBeFalse();
    }

    [Fact]
    public async Task LoadAsync_MultibyteOriginalExceedsByteLimit_RejectsBeforeSending()
    {
        var input = Input.Replace("reviewed text", new string('é', 524_288), StringComparison.Ordinal);
        var sent = false;
        using var http = DashboardPageFixture.Http((_, _) =>
        {
            sent = true;
            return Task.FromResult(DashboardPageFixture.Json("null"));
        });
        dynamic session = DashboardReviewSessionTests.Session(http, TimeProvider.System);
        using var lifetime = (IDisposable)session;

        await session.LoadAsync("workspace", "files", input, "test-capability", "", TestContext.Current.CancellationToken);

        input.Length.ShouldBeLessThan(1_048_576);
        Encoding.UTF8.GetByteCount(input).ShouldBeGreaterThan(1_048_576);
        sent.ShouldBeFalse();
        ((object?)session.Snapshot).ShouldBeNull();
        ((string?)session.Error).ShouldNotBeNull().ShouldContain("maximum 1 MiB");
    }

    [Fact]
    public async Task LoadAsync_CallerCancelsPendingRequest_EndsLoadingWithCancellationMessage()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var http = DashboardPageFixture.Http(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return DashboardPageFixture.Json("null");
        });
        dynamic session = DashboardReviewSessionTests.Session(http, TimeProvider.System);
        using var lifetime = (IDisposable)session;
        Task pending = session.LoadAsync("workspace", "files", Input, "test-capability", "", cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            await cancellation.CancelAsync();
        }

        await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        ((bool)session.Loading).ShouldBeFalse();
        ((object?)session.Snapshot).ShouldBeNull();
        ((string?)session.Error).ShouldBe("Review cancelled or timed out. Verify again; no decision was requested.");
    }

    [Fact]
    public async Task Dispose_ThenLoad_RejectsUseAfterCircuitLifetime()
    {
        using var http = DashboardPageFixture.Http((_, _) => Task.FromResult(DashboardPageFixture.Json("null")));
        dynamic session = DashboardReviewSessionTests.Session(http, TimeProvider.System);
        using var lifetime = (IDisposable)session;
        lifetime.Dispose();

        await Should.ThrowAsync<ObjectDisposedException>(async () =>
        {
            await session.LoadAsync("workspace", "files", Input, "test-capability", "", TestContext.Current.CancellationToken);
        });

        ((bool)session.Loading).ShouldBeFalse();
        ((object?)session.Snapshot).ShouldBeNull();
    }

    private static HttpResponseMessage Reply(Stream stream, string mediaType, long? length)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        response.Content.Headers.ContentLength = length;
        return response;
    }
}
