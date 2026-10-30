using System.Net;
using Weave.Actions.Audit;
using Weave.Actions.Context;
using Weave.Actions.Tests.Helpers;

namespace Weave.Actions.Tests.Audit;

public sealed class GetRecentCapabilityAuditActionTests
{
    [Fact]
    public async Task ExecuteAsync_LiveRows_ReturnsTypedEntries()
    {
        const string body = """
        [
          { "tokenId": "t-1", "grant": "tool:git", "issuedTo": "ada", "workspaceId": "ws-1", "actionContext": "x", "outcome": "Allow", "timestamp": "2026-05-07T12:34:56Z" }
        ]
        """;
        using var client = HttpClientReturning(HttpStatusCode.OK, body);
        var action = new GetRecentCapabilityAuditAction(client);

        var result = await action.ExecuteAsync(new GetRecentCapabilityAuditInput(50), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Entries.Count.ShouldBe(1);
        result.Value.Entries[0].TokenId.ShouldBe("t-1");
    }

    [Fact]
    public async Task ExecuteAsync_TargetsAuditEndpointWithLimit()
    {
        var handler = StubHttpMessageHandler.Returns(HttpStatusCode.OK, "[]");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://example.test") };
        var action = new GetRecentCapabilityAuditAction(client);

        await action.ExecuteAsync(new GetRecentCapabilityAuditInput(100), CancellationToken.None);

        handler.LastRequestUri!.PathAndQuery.ShouldBe("/api/audit/capability?limit=100");
    }

    [Fact]
    public async Task ExecuteAsync_HttpRequestException_ReturnsSiloUnreachable()
    {
        using var client = new HttpClient(StubHttpMessageHandler.Throws(new HttpRequestException("nope")))
        {
            BaseAddress = new Uri("http://example.test")
        };
        var action = new GetRecentCapabilityAuditAction(client);

        var result = await action.ExecuteAsync(new GetRecentCapabilityAuditInput(100), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.SiloUnreachable);
    }

    private static HttpClient HttpClientReturning(HttpStatusCode status, string body)
        => new(StubHttpMessageHandler.Returns(status, body)) { BaseAddress = new Uri("http://example.test") };
}
