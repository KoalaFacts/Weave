using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Weave.Actions.Agent;

namespace Weave.Cli.Tests;

[Collection("Tui view console")]
public sealed class TuiChatPresentationTests
{
    [Theory]
    [InlineData(400, "{\"errors\":{\"content\":[\"message-too-long-fixture\"]}}", "Message rejected: content: message-too-long-fixture")]
    [InlineData(403, "{}", "Silo refused the request: HTTP 403")]
    [InlineData(409, "{\"detail\":\"agent-paused-fixture\"}", "Agent unavailable: agent-paused-fixture")]
    [InlineData(503, "{}", "Silo error: HTTP 503")]
    public async Task SendAsync_RejectedRequest_ExplainsCategoryAndPreservesPreviousHistory(int status, string body, string expected)
    {
        using var context = new TuiViewTestContext();
        context.Open(running: true, agent: "reviewer");
        context.Respond = _ => Sse("event: complete\ndata: {\"content\":\"retained-answer\",\"messages\":[]}\n\n");
        var chat = new TuiChatSession(new SendMessageStreamingAction(context.Client), new FakeTimeProvider());
        await chat.SendAsync(context.Session, "retained-question", TestContext.Current.CancellationToken);
        context.Respond = _ => TuiViewTestContext.Json(body, (HttpStatusCode)status);

        await chat.SendAsync(context.Session, "rejected-question", TestContext.Current.CancellationToken);

        context.Output.ShouldContain(expected);
        chat.History.Select(message => message.Content).ShouldBe(["retained-question", "retained-answer"]);
    }

    [Fact]
    public async Task SendAsync_TransportFailure_LabelsUnreachableWithoutAppendingHistory()
    {
        using var context = new TuiViewTestContext();
        context.Open(running: true, agent: "reviewer");
        context.Respond = _ => throw new HttpRequestException("transport-fixture");
        var chat = new TuiChatSession(new SendMessageStreamingAction(context.Client), new FakeTimeProvider());

        await chat.SendAsync(context.Session, "question", TestContext.Current.CancellationToken);

        context.Output.ShouldContain("Silo unreachable");
        context.Output.ShouldContain("transport-fixture");
        chat.History.ShouldBeEmpty();
    }

    [Fact]
    public async Task SendAsync_CompleteWithToolAndModelMetadata_RendersStreamAndCompletionDetails()
    {
        using var context = new TuiViewTestContext();
        context.Open(running: true, agent: "reviewer");
        context.Respond = _ => Sse("event: text\ndata: {\"text\":\"answer[raw]\"}\n\nevent: complete\ndata: {\"content\":\"canonical-answer\",\"usedTools\":true,\"model\":\"model-fixture\",\"messages\":[]}\n\n");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        var chat = new TuiChatSession(new SendMessageStreamingAction(context.Client), clock);

        await chat.SendAsync(context.Session, "question[raw]", TestContext.Current.CancellationToken);

        context.Output.ShouldContain("question[raw]");
        context.Output.ShouldContain("answer[raw]");
        context.Output.ShouldContain("Tools were used to generate this response.");
        context.Output.ShouldContain("Model: model-fixture");
        chat.History.Select(message => message.Content).ShouldBe(["question[raw]", "canonical-answer"]);
        chat.History.ShouldAllBe(message => message.Timestamp == clock.GetUtcNow());
    }

    [Fact]
    public async Task SendAsync_CancelledRequest_DoesNotReportErrorOrAppendHistory()
    {
        using var context = new TuiViewTestContext();
        context.Open(running: true, agent: "reviewer");
        var chat = new TuiChatSession(new SendMessageStreamingAction(context.Client), new FakeTimeProvider());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await chat.SendAsync(context.Session, "cancelled-question", cancellation.Token);

        chat.History.ShouldBeEmpty();
        context.Requests.ShouldBeEmpty();
        context.Output.ShouldNotContain("Silo error");
        context.Output.ShouldNotContain("Agent call failed");
        context.Output.ShouldNotContain("Silo unreachable");
    }

    [Fact]
    public async Task SendAsync_StoppedWorkspace_ShowsStartHintWithoutHttp()
    {
        using var context = new TuiViewTestContext();
        context.Open(agent: "reviewer");
        var chat = new TuiChatSession(new SendMessageStreamingAction(context.Client), new FakeTimeProvider());

        await chat.SendAsync(context.Session, "question", TestContext.Current.CancellationToken);

        context.Output.ShouldContain("Workspace 'review-space' is not running. Type /up to start it.");
        context.Requests.ShouldBeEmpty();
        chat.History.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null, "No agent selected. Use /use <agent> first.")]
    [InlineData("reviewer", "No conversation history yet. Send a message first.")]
    public void ShowHistory_EmptyConversation_ExplainsNextAction(string? agent, string expected)
    {
        using var context = new TuiViewTestContext();
        context.Open(running: true, agent: agent);
        var chat = new TuiChatSession(new SendMessageStreamingAction(context.Client), new FakeTimeProvider());

        chat.ShowHistory(context.Session);

        context.Output.ShouldContain(expected);
        context.Output.ShouldNotContain("History ·");
        context.Requests.ShouldBeEmpty();
    }

    private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
    };
}
