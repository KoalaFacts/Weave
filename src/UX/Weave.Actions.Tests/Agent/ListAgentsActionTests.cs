using System.Net;
using Weave.Actions.Agent;
using Weave.Actions.Context;
using Weave.Actions.Tests.Helpers;

namespace Weave.Actions.Tests.Agent;

public sealed class ListAgentsActionTests
{
    [Fact]
    public async Task ExecuteAsync_LiveAgents_ReturnsTranslatedSummaries()
    {
        const string body = """
        [
          { "agentName": "alpha", "status": "running", "model": "gpt", "connectedTools": ["git", "files"], "activeTasks": [{}, {}] },
          { "agentName": "beta", "status": "idle", "model": null, "connectedTools": [], "activeTasks": [] }
        ]
        """;
        using var client = HttpClientReturning(HttpStatusCode.OK, body);
        var action = new ListAgentsAction(client);

        var result = await action.ExecuteAsync(new ListAgentsInput("ws-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Agents.Count.ShouldBe(2);
        result.Value.Agents[0].AgentName.ShouldBe("alpha");
        result.Value.Agents[0].Model.ShouldBe("gpt");
        result.Value.Agents[0].ConnectedToolsCount.ShouldBe(2);
        result.Value.Agents[0].ActiveTasksCount.ShouldBe(2);
        result.Value.Agents[1].AgentName.ShouldBe("beta");
        result.Value.Agents[1].Model.ShouldBeNull();
        result.Value.Agents[1].ConnectedToolsCount.ShouldBe(0);
        result.Value.Agents[1].ActiveTasksCount.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyArray_ReturnsSuccessWithEmptyList()
    {
        using var client = HttpClientReturning(HttpStatusCode.OK, "[]");
        var action = new ListAgentsAction(client);

        var result = await action.ExecuteAsync(new ListAgentsInput("ws-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Agents.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_CancellationRequested_ReturnsCancelled()
    {
        using var cts = new CancellationTokenSource();
        // Stub handler that throws OperationCanceledException once the token is signalled.
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://example.test") };
        var action = new ListAgentsAction(client);

        var result = await action.ExecuteAsync(new ListAgentsInput("ws-1"), cts.Token);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
        result.Failure.Reason.ShouldBe(ActionFailureReason.Cancelled);
    }

    [Fact]
    public async Task ExecuteAsync_HttpRequestException_ReturnsSiloUnreachable()
    {
        using var client = HttpClientThrowing(new HttpRequestException("connection refused"));
        var action = new ListAgentsAction(client);

        var result = await action.ExecuteAsync(new ListAgentsInput("ws-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
        result.Failure.Reason.ShouldBe(ActionFailureReason.SiloUnreachable);
        result.Failure.Message.ShouldContain("connection refused");
    }

    [Fact]
    public async Task ExecuteAsync_TargetsAgentsEndpointWithEscapedWorkspaceId()
    {
        var handler = StubHttpMessageHandler.Returns(HttpStatusCode.OK, "[]");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://example.test") };
        var action = new ListAgentsAction(client);

        await action.ExecuteAsync(new ListAgentsInput("ws/with spaces"), CancellationToken.None);

        handler.LastRequestUri!.AbsolutePath.ShouldBe("/api/workspaces/ws%2Fwith%20spaces/agents");
    }

    [Fact]
    public async Task ExecuteAsync_NullInput_Throws()
    {
        using var client = HttpClientReturning(HttpStatusCode.OK, "[]");
        var action = new ListAgentsAction(client);

        await Should.ThrowAsync<ArgumentNullException>(
            () => action.ExecuteAsync(null!, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteAsync_BlankWorkspaceId_Throws(string workspaceId)
    {
        using var client = HttpClientReturning(HttpStatusCode.OK, "[]");
        var action = new ListAgentsAction(client);

        await Should.ThrowAsync<ArgumentException>(
            () => action.ExecuteAsync(new ListAgentsInput(workspaceId), CancellationToken.None));
    }

    private static HttpClient HttpClientReturning(HttpStatusCode status, string body)
        => new(StubHttpMessageHandler.Returns(status, body)) { BaseAddress = new Uri("http://example.test") };

    private static HttpClient HttpClientThrowing(Exception ex)
        => new(StubHttpMessageHandler.Throws(ex)) { BaseAddress = new Uri("http://example.test") };
}
