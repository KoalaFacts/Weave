using System.Net;
using System.Text;
using Weave.Actions.Agent;
using Weave.Actions.Context;
using Weave.Actions.Tests.Helpers;

namespace Weave.Actions.Tests.Agent;

public sealed class SendMessageStreamingActionTests
{
    private const string TextEventBody = """
        event: text
        data: {"text":"Hello"}

        event: text
        data: {"text":", world"}

        event: complete
        data: {"content":"Hello, world.","conversationId":"conv-1","usedTools":false,"model":"gpt-5","messages":[{"role":"user","content":"hi","timestamp":"2026-05-07T10:00:00Z"},{"role":"assistant","content":"Hello, world.","timestamp":"2026-05-07T10:00:01Z"}]}


        """;

    [Fact]
    public async Task StreamAsync_200_YieldsTextChunksThenCompleteWithTranslatedResult()
    {
        using var client = HttpClientWithSse(TextEventBody);
        var action = new SendMessageStreamingAction(client);

        var chunks = await CollectAsync(action, new SendMessageInput("ws-1", "alpha", "hi"));

        chunks.Count.ShouldBe(3);
        chunks[0].ShouldBeOfType<SendMessageTextChunk>().Text.ShouldBe("Hello");
        chunks[1].ShouldBeOfType<SendMessageTextChunk>().Text.ShouldBe(", world");
        var complete = chunks[2].ShouldBeOfType<SendMessageCompleteChunk>();
        complete.Result.Content.ShouldBe("Hello, world.");
        complete.Result.ConversationId.ShouldBe("conv-1");
        complete.Result.Model.ShouldBe("gpt-5");
        complete.Result.Messages.Count.ShouldBe(2);
        complete.Result.Messages[1].Content.ShouldBe("Hello, world.");
    }

    [Fact]
    public async Task StreamAsync_200_StreamEndsAtCompleteEvenIfMoreTextFollows()
    {
        const string body = """
            event: text
            data: {"text":"a"}

            event: complete
            data: {"content":"a","conversationId":"c","usedTools":false,"messages":[]}

            event: text
            data: {"text":"b"}


            """;
        using var client = HttpClientWithSse(body);
        var action = new SendMessageStreamingAction(client);

        var chunks = await CollectAsync(action, new SendMessageInput("ws-1", "alpha", "hi"));

        chunks.Count.ShouldBe(2);
        chunks[0].ShouldBeOfType<SendMessageTextChunk>();
        chunks[1].ShouldBeOfType<SendMessageCompleteChunk>();
    }

    [Fact]
    public async Task StreamAsync_200_StreamEndsWithoutComplete_YieldsInternalError()
    {
        const string body = """
            event: text
            data: {"text":"hi"}


            """;
        using var client = HttpClientWithSse(body);
        var action = new SendMessageStreamingAction(client);

        var chunks = await CollectAsync(action, new SendMessageInput("ws-1", "alpha", "hi"));

        chunks.Count.ShouldBe(2);
        chunks[0].ShouldBeOfType<SendMessageTextChunk>();
        var error = chunks[1].ShouldBeOfType<SendMessageErrorChunk>();
        error.Failure.Reason.ShouldBe(ActionFailureReason.Internal);
        error.Failure.Message.ShouldContain("without a complete event");
    }

    [Fact]
    public async Task StreamAsync_400_YieldsValidationFailureWithExtractedMessage()
    {
        const string body = """
            { "title": "Validation Failed", "errors": { "content": ["Content is required."] } }
            """;
        using var client = HttpClientReturning(HttpStatusCode.BadRequest, body);
        var action = new SendMessageStreamingAction(client);

        var chunks = await CollectAsync(action, new SendMessageInput("ws-1", "alpha", ""));

        chunks.Count.ShouldBe(1);
        var error = chunks[0].ShouldBeOfType<SendMessageErrorChunk>();
        error.Failure.Reason.ShouldBe(ActionFailureReason.ValidationFailed);
        error.Failure.Message.ShouldContain("content: Content is required.");
    }

    [Fact]
    public async Task StreamAsync_409_YieldsConflictWithSiloDetail()
    {
        const string body = """{ "title": "Conflict", "detail": "Agent is paused <<MARKER-FROM-SILO>>." }""";
        using var client = HttpClientReturning(HttpStatusCode.Conflict, body);
        var action = new SendMessageStreamingAction(client);

        var chunks = await CollectAsync(action, new SendMessageInput("ws-1", "alpha", "hi"));

        chunks.Count.ShouldBe(1);
        var error = chunks[0].ShouldBeOfType<SendMessageErrorChunk>();
        error.Failure.Reason.ShouldBe(ActionFailureReason.Conflict);
        error.Failure.Message.ShouldContain("<<MARKER-FROM-SILO>>");
    }

    [Fact]
    public async Task StreamAsync_401_YieldsUnauthorized()
    {
        using var client = HttpClientReturning(HttpStatusCode.Unauthorized, "");
        var action = new SendMessageStreamingAction(client);

        var chunks = await CollectAsync(action, new SendMessageInput("ws-1", "alpha", "hi"));

        chunks.Count.ShouldBe(1);
        chunks[0].ShouldBeOfType<SendMessageErrorChunk>().Failure.Reason.ShouldBe(ActionFailureReason.Unauthorized);
    }

    [Fact]
    public async Task StreamAsync_500_YieldsInternal()
    {
        using var client = HttpClientReturning(HttpStatusCode.InternalServerError, "");
        var action = new SendMessageStreamingAction(client);

        var chunks = await CollectAsync(action, new SendMessageInput("ws-1", "alpha", "hi"));

        chunks.Count.ShouldBe(1);
        chunks[0].ShouldBeOfType<SendMessageErrorChunk>().Failure.Reason.ShouldBe(ActionFailureReason.Internal);
    }

    [Fact]
    public async Task StreamAsync_HttpRequestException_YieldsSiloUnreachable()
    {
        using var client = HttpClientThrowing(new HttpRequestException("connection refused <<NETMARK>>"));
        var action = new SendMessageStreamingAction(client);

        var chunks = await CollectAsync(action, new SendMessageInput("ws-1", "alpha", "hi"));

        chunks.Count.ShouldBe(1);
        var error = chunks[0].ShouldBeOfType<SendMessageErrorChunk>();
        error.Failure.Reason.ShouldBe(ActionFailureReason.SiloUnreachable);
        error.Failure.Message.ShouldContain("<<NETMARK>>");
    }

    [Fact]
    public async Task StreamAsync_TargetsStreamEndpointWithEscapedSegments()
    {
        var handler = SseHandler.Returns(TextEventBody);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://example.test") };
        var action = new SendMessageStreamingAction(client);

        await CollectAsync(action, new SendMessageInput("ws/1", "agent name", "hi"));

        handler.LastRequestMethod.ShouldBe(HttpMethod.Post);
        handler.LastRequestUri!.AbsolutePath.ShouldBe("/api/workspaces/ws%2F1/agents/agent%20name/messages/stream");
    }

    [Fact]
    public async Task StreamAsync_NullInput_Throws()
    {
        using var client = HttpClientReturning(HttpStatusCode.OK, "");
        var action = new SendMessageStreamingAction(client);

        await Should.ThrowAsync<ArgumentNullException>(async () =>
        {
            await foreach (var _ in action.StreamAsync(null!, CancellationToken.None)) { }
        });
    }

    [Theory]
    [InlineData("", "alpha")]
    [InlineData("ws-1", "")]
    public async Task StreamAsync_BlankIdentifier_Throws(string workspaceId, string agentName)
    {
        using var client = HttpClientReturning(HttpStatusCode.OK, "");
        var action = new SendMessageStreamingAction(client);

        await Should.ThrowAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in action.StreamAsync(new SendMessageInput(workspaceId, agentName, "hi"), CancellationToken.None)) { }
        });
    }

    private static async Task<List<SendMessageStreamingChunk>> CollectAsync(
        SendMessageStreamingAction action,
        SendMessageInput input)
    {
        var chunks = new List<SendMessageStreamingChunk>();
        await foreach (var chunk in action.StreamAsync(input, TestContext.Current.CancellationToken))
            chunks.Add(chunk);
        return chunks;
    }

    private static HttpClient HttpClientReturning(HttpStatusCode status, string body)
        => new(StubHttpMessageHandler.Returns(status, body)) { BaseAddress = new Uri("http://example.test") };

    private static HttpClient HttpClientThrowing(Exception ex)
        => new(StubHttpMessageHandler.Throws(ex)) { BaseAddress = new Uri("http://example.test") };

    private static HttpClient HttpClientWithSse(string sseBody)
        => new(SseHandler.Returns(sseBody)) { BaseAddress = new Uri("http://example.test") };

    private sealed class SseHandler : HttpMessageHandler
    {
        private readonly string _body;

        private SseHandler(string body) => _body = body;

        public static SseHandler Returns(string body) => new(body);

        public Uri? LastRequestUri { get; private set; }

        public HttpMethod? LastRequestMethod { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastRequestMethod = request.Method;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "text/event-stream")
            };
            return Task.FromResult(response);
        }
    }
}
