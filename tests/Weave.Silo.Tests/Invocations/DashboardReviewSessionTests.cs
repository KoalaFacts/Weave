using System.Net;
using System.Text;
using System.Text.Json;

namespace Weave.Silo.Tests.Invocations;

public sealed class DashboardReviewSessionTests
{
    private const string Id = "abcdef0123456789abcdef0123456789";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-20T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task LoadAsync_ValidSnapshot_UsesOnlyReviewRouteAndRequestLocalCredentials()
    {
        var calls = 0;
        using var http = Client(async (request, ct) =>
        {
            calls++;
            request.Method.ShouldBe(HttpMethod.Post);
            request.RequestUri!.AbsolutePath.ShouldBe($"/api/workspaces/workspace/tools/files/invocations/{Id}/approval/review");
            request.Headers.GetValues("X-Weave-Capability").Single().ShouldBe("test-capability");
            request.Headers.Authorization!.Parameter.ShouldBe("test-api-token");
            (await request.Content!.ReadAsStringAsync(ct)).ShouldBe(Input());
            return Reply();
        });
        dynamic session = Session(http, new Clock());
        using var lifetime = (IDisposable)session;
        await session.LoadAsync("workspace", "files", Input(), "test-capability", "test-api-token", TestContext.Current.CancellationToken);
        ((string?)session.Error).ShouldBeNull();
        ((object?)session.Snapshot).ShouldNotBeNull();
        ((string)session.Snapshot.RawInput).ShouldBe("reviewed text");
        http.DefaultRequestHeaders.Contains("X-Weave-Capability").ShouldBeFalse();
        http.DefaultRequestHeaders.Authorization.ShouldBeNull();
        calls.ShouldBe(1);
    }

    [Theory]
    [InlineData(302)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(409)]
    [InlineData(500)]
    public async Task LoadAsync_RejectedResponse_DoesNotEchoBodyOrRetry(int status)
    {
        var calls = 0;
        using var http = Client((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent("private-token-and-upstream-body")
            });
        });
        dynamic session = Session(http, new Clock());
        using var lifetime = (IDisposable)session;
        await session.LoadAsync("workspace", "files", Input(), "test-capability", "", TestContext.Current.CancellationToken);
        ((object?)session.Snapshot).ShouldBeNull();
        ((string?)session.Error).ShouldNotBeNull().ShouldNotContain("private-token");
        calls.ShouldBe(1);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("body")]
    [InlineData("expired")]
    public async Task LoadAsync_MismatchedOrExpiredResponse_DoesNotPresentVerifiedContent(string defect)
    {
        using var http = Client((_, _) => Task.FromResult(Reply(
            id: defect == "identity" ? new string('1', 32) : Id,
            body: defect == "body" ? "different" : "reviewed text",
            expiry: defect == "expired" ? Now : Now.AddMinutes(5))));
        dynamic session = Session(http, new Clock());
        using var lifetime = (IDisposable)session;
        await session.LoadAsync("workspace", "files", Input(), "test-capability", "", TestContext.Current.CancellationToken);
        ((object?)session.Snapshot).ShouldBeNull();
        ((string?)session.Error).ShouldNotBeNull();
    }

    [Fact]
    public async Task Reset_InFlightResponse_CannotRestoreAnInvalidatedSnapshot()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = Client((_, _) => { entered.TrySetResult(); return release.Task; });
        dynamic session = Session(http, new Clock());
        using var lifetime = (IDisposable)session;
        Task pending = session.LoadAsync("workspace", "files", Input(), "test-capability", "", TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            session.Reset();
            ((bool)session.Loading).ShouldBeFalse();
        }
        finally
        {
            release.TrySetResult(Reply());
            await pending;
        }
        ((object?)session.Snapshot).ShouldBeNull();
        ((string?)session.Error).ShouldBeNull();
    }

    [Fact]
    public async Task LoadAsync_OlderResponse_CannotOverwriteTheNewerReview()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var http = Client((_, _) =>
        {
            if (++calls == 1) { firstEntered.TrySetResult(); return release.Task; }
            return Task.FromResult(Reply(body: "second content"));
        });
        dynamic session = Session(http, new Clock());
        using var lifetime = (IDisposable)session;
        Task first = session.LoadAsync("workspace", "files", Input(), "test-capability", "", TestContext.Current.CancellationToken);
        try
        {
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await session.LoadAsync("workspace", "files", Input("second content"), "other-credential", "", TestContext.Current.CancellationToken);
        }
        finally
        {
            release.TrySetResult(Reply());
            await first;
        }
        ((string)session.Snapshot.RawInput).ShouldBe("second content");
        ((string?)session.Error).ShouldBeNull();
    }

    [Fact]
    public async Task IsExpired_ClockPassesExpiry_MarksSnapshotWithoutAnotherRequest()
    {
        var clock = new Clock();
        var calls = 0;
        using var http = Client((_, _) => { calls++; return Task.FromResult(Reply()); });
        dynamic session = Session(http, clock);
        using var lifetime = (IDisposable)session;
        await session.LoadAsync("workspace", "files", Input(), "test-capability", "", TestContext.Current.CancellationToken);
        ((bool)session.IsExpired).ShouldBeFalse();
        clock.Current = Now.AddMinutes(5);
        ((bool)session.IsExpired).ShouldBeTrue();
        calls.ShouldBe(1);
        session.Reset();
        ((object?)session.Snapshot).ShouldBeNull();
    }

    [Theory]
    [InlineData("", "test-capability")]
    [InlineData("{invalid-json}", "test-capability")]
    [InlineData("{}", "test-capability")]
    [InlineData("{}", "bad\r\nheader")]
    public async Task LoadAsync_InvalidInput_DoesNotSendRequest(string input, string credential)
    {
        var calls = 0;
        using var http = Client((_, _) => { calls++; return Task.FromResult(Reply()); });
        dynamic session = Session(http, new Clock());
        using var lifetime = (IDisposable)session;
        await session.LoadAsync("workspace", "files", input, credential, "", TestContext.Current.CancellationToken);
        calls.ShouldBe(0);
        ((object?)session.Snapshot).ShouldBeNull();
        ((string?)session.Error).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("http://example.com/")]
    [InlineData("https://user:password@example.com/")]
    [InlineData("https://example.com/?token=secret")]
    [InlineData("https://example.com/prefix")]
    [InlineData("file:///tmp/review")]
    public void ParseUpstream_UnsafeOrAmbiguousOrigin_Rejects(string address)
    {
        var method = DashboardReviewRenderingTests.DashboardType("Weave.Dashboard.Approvals.ApprovalReviewOptions")
            .GetMethod("ParseUpstream")!;
        var error = Should.Throw<System.Reflection.TargetInvocationException>(() => method.Invoke(null, [address]));
        error.InnerException.ShouldBeOfType<InvalidOperationException>();
        error.InnerException.Message.ShouldNotContain(address);
    }

    [Fact]
    public void CreateHandler_DoesNotForwardViaRedirectCookiesOrProxy()
    {
        using var handler = (HttpClientHandler)DashboardReviewRenderingTests.DashboardType("Weave.Dashboard.Approvals.ApprovalReviewOptions")
            .GetMethod("CreateHandler")!.Invoke(null, null)!;
        handler.AllowAutoRedirect.ShouldBeFalse();
        handler.UseCookies.ShouldBeFalse();
        handler.UseProxy.ShouldBeFalse();
        handler.UseDefaultCredentials.ShouldBeFalse();
        handler.ServerCertificateCustomValidationCallback.ShouldBeNull();
    }

    internal static object Session(HttpClient http, TimeProvider clock) => Activator.CreateInstance(
        DashboardReviewRenderingTests.DashboardType("Weave.Dashboard.Approvals.ApprovalReviewSession"), http, clock)!;

    private static string Input(string body = "reviewed text") => JsonSerializer.Serialize(new
    {
        invocationId = Id, toolName = "files", method = "write_file",
        parameters = new Dictionary<string, string> { ["path"] = "note.txt" }, rawInput = body
    });

    private static HttpResponseMessage Reply(string id = Id, string body = "reviewed text", DateTimeOffset? expiry = null) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            invocationId = id, workspaceId = "workspace", subject = "original-agent", toolName = "files", operation = "write_file",
            targetDescription = "FileSystem controlled root", parameters = new Dictionary<string, string> { ["path"] = "note.txt" },
            rawInput = body, planDigest = "test-plan", expiresAt = expiry ?? Now.AddMinutes(5)
        }), Encoding.UTF8, "application/json")
    };

    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new Handler(send)) { BaseAddress = new Uri("http://localhost/") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = Now;
        public override DateTimeOffset GetUtcNow() => Current;
    }
}
