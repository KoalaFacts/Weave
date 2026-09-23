using System.Net;

namespace Weave.Silo.Tests.Invocations;

public sealed partial class TrustedOperatorOnboardingTests
{
    [Fact]
    public async Task Request_OperatorKeyDoesNotReplaceGlobalAuthentication_BothChecksRemainRequired()
    {
        await using var fx = new Fixture(globalAuthentication: true);
        fx.Start();
        using var noGlobal = await fx.SendAsync(HttpMethod.Post, "/api/operator/tools/files/connect", admin: true);
        noGlobal.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        fx.Client.DefaultRequestHeaders.Authorization = new("Bearer", fx.GlobalSecret);
        using var noOperator = await fx.SendAsync(HttpMethod.Post, "/api/operator/tools/files/connect");
        noOperator.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var connected = await fx.SendAsync(HttpMethod.Post, "/api/operator/tools/files/connect", admin: true);
        connected.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var writer = await fx.IssueAsync("writer");
        using var pending = await fx.SendAsync(HttpMethod.Post, Route, Request(), writer);
        pending.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        fx.Client.DefaultRequestHeaders.Authorization = null;
        using var denied = await fx.SendAsync(HttpMethod.Post, Route, Request(), writer);
        denied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        File.ReadAllText(fx.Target).ShouldBe("original");
    }

    [Fact]
    public async Task Request_UntrustedHttpWithSpoofedForwardingHeaders_CannotIssueCredentials()
    {
        await using var fx = new Fixture();
        fx.Start();
        // TestServer supplies no trusted loopback socket identity. A Host header is not one.
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/api/operator/credentials/writer/issue");
        request.Headers.Add("X-Weave-Operator-Key", fx.OperatorKey);
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", "127.0.0.1");
        using var response = await fx.Client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Connect_ProfileBodyCannotReplaceConfiguredTarget_NoFileEffect()
    {
        await using var fx = new Fixture();
        fx.Start();
        using var response = await fx.SendAsync(HttpMethod.Post, "/api/operator/tools/files/connect",
            new { workspaceId = "another-workspace", root = "/" }, admin: true);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        File.ReadAllText(fx.Target).ShouldBe("original");
    }
}
