using System.Net;
using Weave.Actions.Agent;
using Weave.Actions.Context;
using Weave.Actions.Tests.Helpers;

namespace Weave.Actions.Tests.Agent;

public sealed class SendMessageActionTests
{
    [Fact]
    public async Task ExecuteAsync_200_ReturnsTranslatedReply()
    {
        const string body = """
        {
          "content": "Hello, world.",
          "conversationId": "conv-1",
          "usedTools": true,
          "model": "gpt-5",
          "messages": [
            { "role": "user", "content": "hi", "timestamp": "2026-05-07T10:00:00Z" },
            { "role": "assistant", "content": "Hello, world.", "timestamp": "2026-05-07T10:00:01Z" }
          ]
        }
        """;
        using var client = HttpClientReturning(HttpStatusCode.OK, body);
        var action = new SendMessageAction(client);

        var result = await action.ExecuteAsync(new SendMessageInput("ws-1", "alpha", "hi"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Content.ShouldBe("Hello, world.");
        result.Value.ConversationId.ShouldBe("conv-1");
        result.Value.UsedTools.ShouldBeTrue();
        result.Value.Model.ShouldBe("gpt-5");
        result.Value.Messages.Count.ShouldBe(2);
        result.Value.Messages[0].Role.ShouldBe("user");
        result.Value.Messages[1].Content.ShouldBe("Hello, world.");
        result.Value.Messages[0].Timestamp.ShouldBe(new DateTimeOffset(2026, 5, 7, 10, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task ExecuteAsync_400_ReturnsValidationFailedWithExtractedMessage()
    {
        const string body = """
        {
          "title": "Validation Failed",
          "errors": { "content": ["Content is required."] }
        }
        """;
        using var client = HttpClientReturning(HttpStatusCode.BadRequest, body);
        var action = new SendMessageAction(client);

        var result = await action.ExecuteAsync(new SendMessageInput("ws-1", "alpha", ""), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
        result.Failure.Reason.ShouldBe(ActionFailureReason.ValidationFailed);
        result.Failure.Message.ShouldContain("content: Content is required.");
    }

    [Fact]
    public async Task ExecuteAsync_400_NoErrorsMap_FallsBackToDefault()
    {
        const string body = """{ "title": "Bad request" }""";
        using var client = HttpClientReturning(HttpStatusCode.BadRequest, body);
        var action = new SendMessageAction(client);

        var result = await action.ExecuteAsync(new SendMessageInput("ws-1", "alpha", "hi"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.ValidationFailed);
        result.Failure.Message.ShouldBe("Message rejected by the silo.");
    }

    [Fact]
    public async Task ExecuteAsync_409_ReturnsConflictWithSiloDetail()
    {
        // <<MARKER-FROM-SILO>> is unique to the stub — ensures we surface
        // the silo's detail rather than coincidentally matching the
        // action's fallback default.
        const string body = """{ "title": "Conflict", "detail": "Agent is paused <<MARKER-FROM-SILO>>." }""";
        using var client = HttpClientReturning(HttpStatusCode.Conflict, body);
        var action = new SendMessageAction(client);

        var result = await action.ExecuteAsync(new SendMessageInput("ws-1", "alpha", "hi"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.Conflict);
        result.Failure.Message.ShouldContain("<<MARKER-FROM-SILO>>");
    }

    [Fact]
    public async Task ExecuteAsync_409_NoDetail_FallsBackToDefault()
    {
        const string body = """{ "title": "Conflict" }""";
        using var client = HttpClientReturning(HttpStatusCode.Conflict, body);
        var action = new SendMessageAction(client);

        var result = await action.ExecuteAsync(new SendMessageInput("ws-1", "alpha", "hi"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.Conflict);
        result.Failure.Message.ShouldContain("could not handle the message");
    }

    [Fact]
    public async Task ExecuteAsync_401_MapsToUnauthorized()
    {
        using var client = HttpClientReturning(HttpStatusCode.Unauthorized, "");
        var action = new SendMessageAction(client);

        var result = await action.ExecuteAsync(new SendMessageInput("ws-1", "alpha", "hi"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.Unauthorized);
    }

    [Fact]
    public async Task ExecuteAsync_500_MapsToInternal()
    {
        using var client = HttpClientReturning(HttpStatusCode.InternalServerError, "");
        var action = new SendMessageAction(client);

        var result = await action.ExecuteAsync(new SendMessageInput("ws-1", "alpha", "hi"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.Internal);
    }

    [Fact]
    public async Task ExecuteAsync_HttpRequestException_ReturnsSiloUnreachable()
    {
        using var client = HttpClientThrowing(new HttpRequestException("connection refused"));
        var action = new SendMessageAction(client);

        var result = await action.ExecuteAsync(new SendMessageInput("ws-1", "alpha", "hi"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
        result.Failure.Reason.ShouldBe(ActionFailureReason.SiloUnreachable);
        result.Failure.Message.ShouldContain("connection refused");
    }

    [Fact]
    public async Task ExecuteAsync_TargetsAgentMessagesEndpointWithEscapedSegments()
    {
        const string body = """{"content":"x","conversationId":"c","usedTools":false,"messages":[]}""";
        var handler = StubHttpMessageHandler.Returns(HttpStatusCode.OK, body);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://example.test") };
        var action = new SendMessageAction(client);

        await action.ExecuteAsync(new SendMessageInput("ws/with spaces", "agent/with spaces", "hi"), CancellationToken.None);

        handler.LastRequestUri!.AbsolutePath.ShouldBe("/api/workspaces/ws%2Fwith%20spaces/agents/agent%2Fwith%20spaces/messages");
    }

    [Fact]
    public async Task ExecuteAsync_UsesPostVerb()
    {
        const string body = """{"content":"x","conversationId":"c","usedTools":false,"messages":[]}""";
        var handler = StubHttpMessageHandler.Returns(HttpStatusCode.OK, body);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://example.test") };
        var action = new SendMessageAction(client);

        await action.ExecuteAsync(new SendMessageInput("ws-1", "alpha", "hi"), CancellationToken.None);

        handler.LastRequestMethod.ShouldBe(HttpMethod.Post);
    }

    [Fact]
    public async Task ExecuteAsync_NullInput_Throws()
    {
        using var client = HttpClientReturning(HttpStatusCode.OK, "{}");
        var action = new SendMessageAction(client);

        await Should.ThrowAsync<ArgumentNullException>(
            () => action.ExecuteAsync(null!, CancellationToken.None));
    }

    [Theory]
    [InlineData("", "alpha")]
    [InlineData("   ", "alpha")]
    [InlineData("ws-1", "")]
    [InlineData("ws-1", "   ")]
    public async Task ExecuteAsync_BlankIdentifier_Throws(string workspaceId, string agentName)
    {
        using var client = HttpClientReturning(HttpStatusCode.OK, "{}");
        var action = new SendMessageAction(client);

        await Should.ThrowAsync<ArgumentException>(
            () => action.ExecuteAsync(new SendMessageInput(workspaceId, agentName, "hi"), CancellationToken.None));
    }

    private static HttpClient HttpClientReturning(HttpStatusCode status, string body)
        => new(StubHttpMessageHandler.Returns(status, body)) { BaseAddress = new Uri("http://example.test") };

    private static HttpClient HttpClientThrowing(Exception ex)
        => new(StubHttpMessageHandler.Throws(ex)) { BaseAddress = new Uri("http://example.test") };
}
