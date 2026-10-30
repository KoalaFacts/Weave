using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Weave.Cli.Commands.Local;

namespace Weave.Cli.Tests;

public sealed class LocalReviewRenewalTests
{
    private const string Id = "82c07b3d2a3646e88f2f0b8db07c452b";
    private static readonly string Digest = "approval-v1:" + new string('A', 64);

    [Theory]
    [InlineData("approve", "Approved")]
    [InlineData("reject", "Rejected")]
    public async Task RunAsync_HumanWaitExceedsCredentialLifetime_RenewsOnlyAfterExactConfirmation(string decision, string state)
    {
        var clock = new FakeTimeProvider();
        var credentialExpiry = clock.GetUtcNow() + TimeSpan.FromMinutes(30);
        var planExpiry = clock.GetUtcNow() + TimeSpan.FromDays(1);
        var issued = 0;
        using var handler = new LocalHttpFixture((request, body) =>
        {
            var route = request.RequestUri!.AbsolutePath;
            if (route.EndsWith("/connect", StringComparison.Ordinal))
                return LocalHttpFixture.Response(204);
            if (route.EndsWith("/review", StringComparison.Ordinal))
            {
                request.Headers.GetValues("X-Weave-Capability").Single().ShouldBe("initial-reviewer");
                clock.GetUtcNow().ShouldBeLessThan(credentialExpiry);
                return LocalHttpFixture.Response(200, Preview(planExpiry));
            }
            if (route == "/api/operator/credentials/reviewer/issue")
            {
                clock.GetUtcNow().ShouldBeGreaterThan(credentialExpiry);
                request.Headers.Contains("X-Weave-Capability").ShouldBeFalse();
                request.Headers.GetValues("X-Weave-Operator-Key").Single().ShouldBe("operator");
                issued++;
                return Credential();
            }
            route.ShouldEndWith("/" + Id + "/decision");
            if (request.Headers.GetValues("X-Weave-Capability").Single() != "fresh-reviewer")
                return LocalHttpFixture.Response(401);
            clock.GetUtcNow().ShouldBeLessThan(planExpiry);
            body!["decision"]!.GetValue<string>().ShouldBe(decision);
            body["planDigest"]!.GetValue<string>().ShouldBe(Digest);
            return LocalHttpFixture.Response(200, new JsonObject { ["invocationId"] = Id, ["approvalState"] = state });
        });
        var terminal = new WaitingTerminal(() =>
        {
            issued.ShouldBe(0);
            handler.Requests.Count.ShouldBe(2);
            clock.Advance(TimeSpan.FromMinutes(31));
            return decision + " " + Digest;
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9401") };
        var result = await new LocalReview(terminal).RunAsync(new LocalHttp(client, clock), "onboarding", Id,
            "initial-reviewer", "operator", TestContext.Current.CancellationToken);
        result.ShouldBe(decision == "approve" ? LocalReviewOutcome.Approved : LocalReviewOutcome.Rejected);
        issued.ShouldBe(1);
        handler.Requests.Count(request => request.Path.EndsWith("/decision", StringComparison.Ordinal)).ShouldBe(1);
        handler.Requests.Any(request => request.Path.EndsWith("/resume", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_ToolCollectedBeforeReviewAndDuringWait_ReconnectsBeforeEachReviewOperation()
    {
        var connected = false;
        var connects = 0;
        using var handler = new LocalHttpFixture((request, body) =>
        {
            var route = request.RequestUri!.AbsolutePath;
            if (route.EndsWith("/connect", StringComparison.Ordinal))
            {
                request.Headers.Contains("X-Weave-Capability").ShouldBeFalse();
                request.Headers.GetValues("X-Weave-Operator-Key").Single().ShouldBe("operator");
                connected = true;
                connects++;
                return LocalHttpFixture.Response(204);
            }
            if (route.EndsWith("/issue", StringComparison.Ordinal))
                return Credential();
            if (!connected)
                return LocalHttpFixture.Response(409, new JsonObject { ["errorCode"] = "approval-review-unavailable" });
            if (route.EndsWith("/review", StringComparison.Ordinal))
                return LocalHttpFixture.Response(200, Preview(DateTimeOffset.MaxValue));
            route.ShouldEndWith("/" + Id + "/decision");
            body!["planDigest"]!.GetValue<string>().ShouldBe(Digest);
            return LocalHttpFixture.Response(200, new JsonObject { ["invocationId"] = Id, ["approvalState"] = "Approved" });
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9401") };
        var terminal = new WaitingTerminal(() => { connected = false; return "approve " + Digest; });
        (await new LocalReview(terminal).RunAsync(new LocalHttp(client, TimeProvider.System), "onboarding", Id,
            "initial-reviewer", "operator", TestContext.Current.CancellationToken)).ShouldBe(LocalReviewOutcome.Approved);
        connects.ShouldBe(2);
        handler.Requests.Count(request => request.Path.EndsWith("/decision", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(500)]
    public async Task RunAsync_ReviewerRenewalDenied_NeverSendsDecision(int status)
    {
        using var handler = new LocalHttpFixture((request, _) => request.RequestUri!.AbsolutePath.EndsWith("/connect", StringComparison.Ordinal)
            ? LocalHttpFixture.Response(204) : request.Method == HttpMethod.Get
            ? LocalHttpFixture.Response(200, Preview(DateTimeOffset.MaxValue))
            : LocalHttpFixture.Response(status));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9401") };
        await Should.ThrowAsync<HttpRequestException>(() => new LocalReview(new WaitingTerminal(() => "approve " + Digest))
            .RunAsync(new LocalHttp(client, TimeProvider.System), "onboarding", Id,
                "initial-reviewer", "operator", TestContext.Current.CancellationToken));
        handler.Requests.Count(request => request.Path.EndsWith("/reviewer/issue", StringComparison.Ordinal)).ShouldBe(1);
        handler.Requests.Any(request => request.Path.EndsWith("/decision", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_CancelledDuringHumanWait_NeverRenewsOrDecides()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new LocalHttpFixture((request, _) => request.RequestUri!.AbsolutePath.EndsWith("/connect", StringComparison.Ordinal)
            ? LocalHttpFixture.Response(204) : LocalHttpFixture.Response(200, Preview(DateTimeOffset.MaxValue)));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9401") };
        var terminal = new WaitingTerminal(() =>
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            return "approve " + Digest;
        });
        await Should.ThrowAsync<OperationCanceledException>(() => new LocalReview(terminal)
            .RunAsync(new LocalHttp(client, TimeProvider.System), "onboarding", Id,
                "initial-reviewer", "operator", cancellation.Token));
        handler.Requests.Count.ShouldBe(2);
        handler.Requests.Any(request => request.Method == "POST" && !request.Path.EndsWith("/connect", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RunAsync_ReconnectionDenied_NeverSendsDecision(int deniedConnection)
    {
        var connects = 0;
        var waited = false;
        using var handler = new LocalHttpFixture((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/connect", StringComparison.Ordinal))
                return LocalHttpFixture.Response(++connects == deniedConnection ? 403 : 204);
            return LocalHttpFixture.Response(200, Preview(DateTimeOffset.MaxValue));
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9401") };
        var terminal = new WaitingTerminal(() => { waited = true; return "approve " + Digest; });
        await Should.ThrowAsync<HttpRequestException>(() => new LocalReview(terminal).RunAsync(new LocalHttp(client, TimeProvider.System),
            "onboarding", Id, "reviewer", "operator", TestContext.Current.CancellationToken));
        connects.ShouldBe(deniedConnection);
        waited.ShouldBe(deniedConnection == 2);
        handler.Requests.Any(request => request.Path.EndsWith("/decision", StringComparison.Ordinal)
            || request.Path.EndsWith("/issue", StringComparison.Ordinal)).ShouldBeFalse();
    }

    private static HttpResponseMessage Credential()
    {
        var response = LocalHttpFixture.Response(200);
        response.Content = new StringContent("fresh-reviewer", Encoding.UTF8, "text/plain");
        response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        return response;
    }

    private static JsonObject Preview(DateTimeOffset expiry) => new()
    {
        ["invocationId"] = Id,
        ["workspaceId"] = "onboarding",
        ["toolName"] = "files",
        ["operation"] = "write_file",
        ["planDigest"] = Digest,
        ["subject"] = "document-agent",
        ["targetDescription"] = "document root",
        ["expiresAt"] = expiry.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        ["parameters"] = new JsonObject { ["path"] = "checklist.md" },
        ["rawInput"] = "中文提案"
    };

    private sealed class WaitingTerminal(Func<string> answer) : ILocalReviewConsole
    {
        public bool IsInteractive => true;
        public void WriteLine(string text) { }
        public ValueTask<string?> ReadLineAsync(CancellationToken ct) => ValueTask.FromResult<string?>(answer());
    }
}
