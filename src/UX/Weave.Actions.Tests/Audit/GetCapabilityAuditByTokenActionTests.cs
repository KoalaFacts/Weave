using System.Net;
using Weave.Actions.Audit;
using Weave.Actions.Context;
using Weave.Actions.Tests.Helpers;

namespace Weave.Actions.Tests.Audit;

public sealed class GetCapabilityAuditByTokenActionTests
{
    [Fact]
    public async Task ExecuteAsync_LiveRows_ReturnsTypedEntries()
    {
        const string body = """
        [
          { "tokenId": "t-1", "grant": "tool:git", "issuedTo": "ada", "workspaceId": "ws-1", "actionContext": "ToolActor.Run:tool:git", "outcome": "Allow", "timestamp": "2026-05-07T12:34:56Z" },
          { "tokenId": "t-1", "grant": "tool:files", "issuedTo": "ada", "workspaceId": "ws-1", "actionContext": "ToolActor.Run:tool:files", "outcome": "Deny", "reason": "grant-missing", "timestamp": "2026-05-07T12:35:00Z" }
        ]
        """;
        using var client = HttpClientReturning(HttpStatusCode.OK, body);
        var action = new GetCapabilityAuditByTokenAction(client);

        var result = await action.ExecuteAsync(new GetCapabilityAuditByTokenInput("t-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Entries.Count.ShouldBe(2);
        result.Value.Entries[0].Outcome.ShouldBe("Allow");
        result.Value.Entries[0].Reason.ShouldBeNull();
        result.Value.Entries[1].Outcome.ShouldBe("Deny");
        result.Value.Entries[1].Reason.ShouldBe("grant-missing");
    }

    [Fact]
    public async Task ExecuteAsync_EmptyArray_ReturnsSuccessWithEmptyList()
    {
        using var client = HttpClientReturning(HttpStatusCode.OK, "[]");
        var action = new GetCapabilityAuditByTokenAction(client);

        var result = await action.ExecuteAsync(new GetCapabilityAuditByTokenInput("t-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_TargetsAuditEndpointWithEscapedTokenId()
    {
        var handler = StubHttpMessageHandler.Returns(HttpStatusCode.OK, "[]");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://example.test") };
        var action = new GetCapabilityAuditByTokenAction(client);

        await action.ExecuteAsync(new GetCapabilityAuditByTokenInput("token/with spaces"), CancellationToken.None);

        handler.LastRequestUri!.AbsolutePath.ShouldBe("/api/audit/capability/token%2Fwith%20spaces");
    }

    [Fact]
    public async Task ExecuteAsync_HttpRequestException_ReturnsSiloUnreachable()
    {
        using var client = new HttpClient(StubHttpMessageHandler.Throws(new HttpRequestException("nope")))
        {
            BaseAddress = new Uri("http://example.test")
        };
        var action = new GetCapabilityAuditByTokenAction(client);

        var result = await action.ExecuteAsync(new GetCapabilityAuditByTokenInput("t-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.SiloUnreachable);
    }

    private static HttpClient HttpClientReturning(HttpStatusCode status, string body)
        => new(StubHttpMessageHandler.Returns(status, body)) { BaseAddress = new Uri("http://example.test") };
}
