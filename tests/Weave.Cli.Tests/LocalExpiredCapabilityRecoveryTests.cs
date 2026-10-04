using System.Text.Json.Nodes;
using Shouldly;
using Weave.Cli.Commands.Local;

namespace Weave.Cli.Tests;

public sealed class LocalExpiredCapabilityRecoveryTests
{
    private const string Id = "82c07b3d2a3646e88f2f0b8db07c452b";

    [Fact]
    public async Task SubmitWrite_CapabilityExpiresBeforeAdmission_FreshClientCanExplicitlySubmitOriginalOnly()
    {
        using var files = new LocalTestDirectory();
        var posts = 0;
        using var handler = new LocalHttpFixture((request, _) =>
        {
            var authority = request.Headers.GetValues("X-Weave-Capability").Single();
            if (request.Method == HttpMethod.Post)
            {
                posts++;
                if (posts == 1)
                {
                    authority.ShouldBe("expiring-agent");
                    return LocalHttpFixture.Response(401, new JsonObject { ["errorCode"] = "invalid-capability" });
                }
                authority.ShouldBe("fresh-agent");
                return LocalHttpFixture.Response(202, new JsonObject { ["invocationId"] = Id, ["approvalState"] = "Pending" });
            }
            // The rejected credential cannot prove absence after it expires.
            if (posts > 0 && authority == "expiring-agent")
                return LocalHttpFixture.Response(401, new JsonObject { ["errorCode"] = "invalid-capability" });
            return Missing(request);
        });
        var original = Original();
        using (var expiredClient = Client(handler))
        {
            var expired = Invocation(expiredClient, "expiring-agent", files.Private);
            var rejected = await expired.CallAsync("submit_write", original, TestContext.Current.CancellationToken);
            rejected["http_status"]!.GetValue<int>().ShouldBe(401);
            posts.ShouldBe(1);
        }

        using var freshClient = Client(handler);
        var fresh = Invocation(freshClient, "fresh-agent", files.Private);
        var changed = (JsonObject)original.DeepClone();
        changed["content"] = "replacement";
        await Should.ThrowAsync<ArgumentException>(() => fresh.CallAsync("submit_write", changed, TestContext.Current.CancellationToken));
        var accepted = await fresh.CallAsync("submit_write", original, TestContext.Current.CancellationToken);
        await fresh.CallAsync("submit_write", original, TestContext.Current.CancellationToken);

        accepted["http_status"]!.GetValue<int>().ShouldBe(202);
        accepted["invocation_id"]!.GetValue<string>().ShouldBe(Id);
        posts.ShouldBe(2);
        var submissions = handler.Requests.Where(request => request.Method == "POST").ToArray();
        submissions[1].Body!.ToJsonString().ShouldBe(submissions[0].Body!.ToJsonString());
        submissions[1].Body!["invocationId"]!.GetValue<string>().ShouldBe(Id);
        submissions[1].Body!["rawInput"]!.GetValue<string>().ShouldBe("original frozen content");
        handler.Requests.Any(request => request.Operator).ShouldBeFalse();
    }

    [Theory]
    [InlineData(401, null)]
    [InlineData(401, "unknown-denial")]
    [InlineData(403, "invalid-capability")]
    [InlineData(503, "invalid-capability")]
    public async Task SubmitWrite_AuthenticationFailureNotDefinitivelyPreAdmission_FreshClientCannotReplay(int status, string? error)
    {
        using var files = new LocalTestDirectory();
        using var handler = new LocalHttpFixture((request, _) => request.Method == HttpMethod.Post
            ? LocalHttpFixture.Response(status, error is null ? null : new JsonObject { ["errorCode"] = error })
            : Missing(request));
        var original = Original();
        using (var expiredClient = Client(handler))
            await Invocation(expiredClient, "old-agent", files.Private).CallAsync("submit_write", original, TestContext.Current.CancellationToken);
        using var freshClient = Client(handler);

        await Invocation(freshClient, "fresh-agent", files.Private).CallAsync("submit_write", original, TestContext.Current.CancellationToken);

        handler.Requests.Count(request => request.Method == "POST").ShouldBe(1);
        File.Exists(Path.Join(files.Private, Id + ".sha256")).ShouldBeTrue();
    }

    [Theory]
    [InlineData(401, "invalid-capability")]
    [InlineData(403, "forbidden")]
    [InlineData(404, "route-not-found")]
    [InlineData(503, "service-unavailable")]
    [InlineData(200, null)]
    public async Task SubmitWrite_DefinitiveRejectionButFreshLookupUnconfirmedOrRecorded_DoesNotReplay(int status, string? error)
    {
        using var files = new LocalTestDirectory();
        var rejected = false;
        using var handler = new LocalHttpFixture((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                rejected = true;
                return LocalHttpFixture.Response(401, new JsonObject { ["errorCode"] = "invalid-capability" });
            }
            return rejected ? LocalHttpFixture.Response(status, error is not null
                ? new JsonObject { ["errorCode"] = error }
                : new JsonObject { ["invocationId"] = Id, ["outcome"] = "OutcomeUnknown", ["outcomeRecorded"] = false })
                : Missing(request);
        });
        var original = Original();
        using (var expiredClient = Client(handler))
            await Invocation(expiredClient, "old-agent", files.Private).CallAsync("submit_write", original, TestContext.Current.CancellationToken);
        using var freshClient = Client(handler);

        await Invocation(freshClient, "fresh-agent", files.Private).CallAsync("submit_write", original, TestContext.Current.CancellationToken);

        handler.Requests.Count(request => request.Method == "POST").ShouldBe(1);
    }

    [Fact]
    public async Task SubmitWrite_ResponseLost_FreshClientPreservesUnknownReceiptWithoutReplay()
    {
        using var files = new LocalTestDirectory();
        using var handler = new LocalHttpFixture((request, _) => request.Method == HttpMethod.Post
            ? throw new HttpRequestException("response lost after possible admission") : Missing(request));
        var original = Original();
        using (var expiredClient = Client(handler))
            await Should.ThrowAsync<HttpRequestException>(() => Invocation(expiredClient, "old-agent", files.Private)
                .CallAsync("submit_write", original, TestContext.Current.CancellationToken));
        using var freshClient = Client(handler);

        await Invocation(freshClient, "fresh-agent", files.Private).CallAsync("submit_write", original, TestContext.Current.CancellationToken);

        handler.Requests.Count(request => request.Method == "POST").ShouldBe(1);
        File.Exists(Path.Join(files.Private, Id + ".sha256")).ShouldBeTrue();
    }

    private static JsonObject Original() => new()
    {
        ["invocation_id"] = Id,
        ["path"] = "summary.md",
        ["content"] = "original frozen content"
    };

    private static HttpResponseMessage Missing(HttpRequestMessage request) => LocalHttpFixture.Response(404, new JsonObject
    {
        ["errorCode"] = request.RequestUri!.AbsolutePath.EndsWith("/approval", StringComparison.Ordinal)
            ? "approval-not-found" : "invocation-not-found"
    });

    private static HttpClient Client(LocalHttpFixture handler) => new(handler, disposeHandler: false)
    {
        BaseAddress = new Uri("http://127.0.0.1:9401")
    };

    private static LocalInvocationClient Invocation(HttpClient client, string capability, string receipts) =>
        new(new LocalHttp(client, TimeProvider.System), "onboarding", capability, receipts);
}
