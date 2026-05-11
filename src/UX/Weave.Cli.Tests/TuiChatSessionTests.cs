using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Weave.Actions.Agent;
using Weave.Cli.Tui;

namespace Weave.Cli.Tests;

/// <summary>
/// Integration tests for <see cref="TuiChatSession.SendAsync"/> after the Phase 3c
/// streaming rewrite. Drives the underlying <see cref="SendMessageStreamingAction"/>
/// through a stub HTTP handler that returns canned SSE bodies, then asserts on
/// the session's history. AnsiConsole output goes to the test runner — assertions
/// only cover history state, which is the side-effect that matters for correctness.
/// </summary>
public sealed class TuiChatSessionTests
{
    private static readonly DateTimeOffset FrozenNow = new(2026, 5, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SendAsync_StreamingComplete_AppendsUserAndAssistantToHistory()
    {
        const string sse = """
            event: text
            data: {"text":"Hello"}

            event: complete
            data: {"content":"Hello, world.","conversationId":"c1","usedTools":false,"messages":[]}


            """;
        var chat = NewChatSession(sse);
        var tuiSession = NewReadyTuiSession();

        await chat.SendAsync(tuiSession, "hi", CancellationToken.None);

        chat.History.Count.ShouldBe(2);
        chat.History[0].Role.ShouldBe("user");
        chat.History[0].Content.ShouldBe("hi");
        chat.History[0].Timestamp.ShouldBe(FrozenNow);
        chat.History[1].Role.ShouldBe("assistant");
        chat.History[1].Content.ShouldBe("Hello, world.");
        chat.History[1].Timestamp.ShouldBe(FrozenNow);
    }

    [Fact]
    public async Task SendAsync_CompleteWithCanonicalMessages_ReplacesHistory()
    {
        const string sse = """
            event: complete
            data: {"content":"reply","conversationId":"c1","usedTools":false,"messages":[{"role":"user","content":"<<CANON-USER>>","timestamp":"2026-05-07T10:00:00Z"},{"role":"assistant","content":"<<CANON-ASSISTANT>>","timestamp":"2026-05-07T10:00:01Z"}]}


            """;
        var chat = NewChatSession(sse);
        var tuiSession = NewReadyTuiSession();

        await chat.SendAsync(tuiSession, "hi", CancellationToken.None);

        chat.History.Count.ShouldBe(2);
        chat.History[0].Content.ShouldBe("<<CANON-USER>>");
        chat.History[1].Content.ShouldBe("<<CANON-ASSISTANT>>");
    }

    [Fact]
    public async Task SendAsync_ErrorChunk_LeavesHistoryUnchanged()
    {
        const string problemBody = """{"title":"Conflict","detail":"Agent is paused."}""";
        var chat = NewChatSession(problemBody, HttpStatusCode.Conflict, isSse: false);
        var tuiSession = NewReadyTuiSession();

        await chat.SendAsync(tuiSession, "hi", CancellationToken.None);

        chat.History.Count.ShouldBe(0);
    }

    [Fact]
    public async Task SendAsync_NoWorkspaceOpen_DoesNotInvokeStreamingAction()
    {
        const string sse = """
            event: complete
            data: {"content":"reply","conversationId":"c1","usedTools":false,"messages":[]}


            """;
        var handler = new CountingSseHandler(sse);
        var chat = NewChatSession(handler);
        var tuiSession = NewTuiSession(resolver: _ => null);

        await chat.SendAsync(tuiSession, "hi", CancellationToken.None);

        chat.History.Count.ShouldBe(0);
        handler.RequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task SendAsync_NoAgentSelected_DoesNotInvokeStreamingAction()
    {
        var handler = new CountingSseHandler("event: complete\ndata: {}\n\n");
        var chat = NewChatSession(handler);
        var tuiSession = NewReadyTuiSession();
        tuiSession.AgentName = null;

        await chat.SendAsync(tuiSession, "hi", CancellationToken.None);

        chat.History.Count.ShouldBe(0);
        handler.RequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task SendAsync_StreamingActionInvokedExactlyOncePerCall()
    {
        const string sse = """
            event: complete
            data: {"content":"reply","conversationId":"c1","usedTools":false,"messages":[]}


            """;
        var handler = new CountingSseHandler(sse);
        var chat = NewChatSession(handler);
        var tuiSession = NewReadyTuiSession();

        await chat.SendAsync(tuiSession, "first", CancellationToken.None);
        await chat.SendAsync(tuiSession, "second", CancellationToken.None);

        handler.RequestCount.ShouldBe(2);
    }

    private static TuiChatSession NewChatSession(string body, HttpStatusCode status = HttpStatusCode.OK, bool isSse = true)
        => NewChatSession(new CountingSseHandler(body, status, isSse));

    private static TuiChatSession NewChatSession(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://example.test") };
        var streamingAction = new SendMessageStreamingAction(http);
        var time = new FakeTimeProvider(FrozenNow);
        return new TuiChatSession(streamingAction, time);
    }

    private static TuiSession NewReadyTuiSession()
    {
        var session = NewTuiSession(resolver: name => $"/tmp/{name}/workspace.json");
        session.TryOpen("ws-test", out _);
        session.MarkRunning("ws-1");
        session.AgentName = "alpha";
        return session;
    }

    private static TuiSession NewTuiSession(Func<string?, string?> resolver)
        => new(new StubManifestResolver(resolver));

    private sealed class StubManifestResolver : IManifestResolver
    {
        private readonly Func<string?, string?> _resolve;

        public StubManifestResolver(Func<string?, string?> resolve) => _resolve = resolve;

        public string? Resolve(string? workspace) => _resolve(workspace);
    }

    private sealed class CountingSseHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;
        private readonly string _contentType;

        public CountingSseHandler(string body, HttpStatusCode status = HttpStatusCode.OK, bool isSse = true)
        {
            _body = body;
            _status = status;
            _contentType = isSse ? "text/event-stream" : "application/json";
        }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, _contentType)
            };
            return Task.FromResult(response);
        }
    }
}
