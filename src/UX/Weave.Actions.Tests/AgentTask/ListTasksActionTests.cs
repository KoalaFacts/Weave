using System.Net;
using Weave.Actions.AgentTask;
using Weave.Actions.Context;
using Weave.Actions.Tests.Helpers;

namespace Weave.Actions.Tests.AgentTask;

public sealed class ListTasksActionTests
{
    [Fact]
    public async Task ExecuteAsync_LiveTasks_ReturnsTranslatedSummaries()
    {
        const string body = """
        [
          { "taskId": "t-1", "description": "do the thing", "status": "Active", "createdAt": "2026-05-06T10:00:00Z", "completedAt": null },
          { "taskId": "t-2", "description": "review", "status": "AwaitingReview", "createdAt": "2026-05-06T11:00:00Z", "completedAt": "2026-05-06T11:30:00Z" }
        ]
        """;
        using var client = HttpClientReturning(HttpStatusCode.OK, body);
        var action = new ListTasksAction(client);

        var result = await action.ExecuteAsync(new ListTasksInput("ws-1", "alpha"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Tasks.Count.ShouldBe(2);
        result.Value.Tasks[0].TaskId.ShouldBe("t-1");
        result.Value.Tasks[0].Description.ShouldBe("do the thing");
        result.Value.Tasks[0].Status.ShouldBe("Active");
        result.Value.Tasks[0].CompletedAt.ShouldBeNull();
        result.Value.Tasks[1].TaskId.ShouldBe("t-2");
        result.Value.Tasks[1].Status.ShouldBe("AwaitingReview");
        result.Value.Tasks[1].CompletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_EmptyArray_ReturnsSuccessWithEmptyList()
    {
        using var client = HttpClientReturning(HttpStatusCode.OK, "[]");
        var action = new ListTasksAction(client);

        var result = await action.ExecuteAsync(new ListTasksInput("ws-1", "alpha"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Tasks.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_HttpRequestException_ReturnsSiloUnreachable()
    {
        using var client = HttpClientThrowing(new HttpRequestException("connection refused"));
        var action = new ListTasksAction(client);

        var result = await action.ExecuteAsync(new ListTasksInput("ws-1", "alpha"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
        result.Failure.Reason.ShouldBe(ActionFailureReason.SiloUnreachable);
        result.Failure.Message.ShouldContain("connection refused");
    }

    [Fact]
    public async Task ExecuteAsync_TargetsTasksEndpointWithEscapedIdentifiers()
    {
        var handler = StubHttpMessageHandler.Returns(HttpStatusCode.OK, "[]");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://example.test") };
        var action = new ListTasksAction(client);

        await action.ExecuteAsync(new ListTasksInput("ws/space", "agent name"), CancellationToken.None);

        handler.LastRequestUri!.AbsolutePath.ShouldBe("/api/workspaces/ws%2Fspace/agents/agent%20name/tasks");
    }

    [Fact]
    public async Task ExecuteAsync_NullInput_Throws()
    {
        using var client = HttpClientReturning(HttpStatusCode.OK, "[]");
        var action = new ListTasksAction(client);

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
        using var client = HttpClientReturning(HttpStatusCode.OK, "[]");
        var action = new ListTasksAction(client);

        await Should.ThrowAsync<ArgumentException>(
            () => action.ExecuteAsync(new ListTasksInput(workspaceId, agentName), CancellationToken.None));
    }

    private static HttpClient HttpClientReturning(HttpStatusCode status, string body)
        => new(StubHttpMessageHandler.Returns(status, body)) { BaseAddress = new Uri("http://example.test") };

    private static HttpClient HttpClientThrowing(Exception ex)
        => new(StubHttpMessageHandler.Throws(ex)) { BaseAddress = new Uri("http://example.test") };
}
