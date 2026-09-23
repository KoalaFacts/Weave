using System.Net;
using Weave.Actions.Agent;
using Weave.Actions.Context;
using Weave.Actions.Tests.Helpers;
using Weave.Actions.Tool;
using Weave.Actions.Workspace;

namespace Weave.Actions.Tests.Workspace;

public sealed class WatchWorkspaceActionTests
{
    [Fact]
    public async Task ExecuteAsync_AllSucceed_ReturnsCompositeSnapshot()
    {
        var action = new WatchWorkspaceAction(
            new GetWorkspaceStatusAction(HttpClient(HttpStatusCode.OK,
                """{"workspaceId":"ws-1","name":"demo","status":"Running","containerCount":2}""")),
            new ListAgentsAction(HttpClient(HttpStatusCode.OK,
                """[{"agentName":"alpha","status":"Idle","model":"gpt-5","connectedTools":[],"activeTasks":[]}]""")),
            new ListToolsAction(HttpClient(HttpStatusCode.OK,
                """[{"toolName":"git","toolType":"cli","status":"Connected","endpoint":"git","connectedAt":null,"errorMessage":null}]""")));

        var result = await action.ExecuteAsync(new WatchWorkspaceInput("ws-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Workspace.WorkspaceId.ShouldBe("ws-1");
        result.Value.Workspace.Status.ShouldBe("Running");
        result.Value.Agents.Count.ShouldBe(1);
        result.Value.Agents[0].AgentName.ShouldBe("alpha");
        result.Value.Tools.Count.ShouldBe(1);
        result.Value.Tools[0].ToolName.ShouldBe("git");
    }

    [Fact]
    public async Task ExecuteAsync_StatusFails_ReturnsThatFailure()
    {
        var action = new WatchWorkspaceAction(
            new GetWorkspaceStatusAction(HttpClient(HttpStatusCode.NotFound, "{}")),
            new ListAgentsAction(HttpClient(HttpStatusCode.OK, "[]")),
            new ListToolsAction(HttpClient(HttpStatusCode.OK, "[]")));

        var result = await action.ExecuteAsync(new WatchWorkspaceInput("ws-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
        // Status's NotFound bubbles up directly — Watch doesn't recharacterize.
        result.Failure.Reason.ShouldBe(ActionFailureReason.NotFound);
        result.Failure.Message.ShouldContain("ws-1");
    }

    [Fact]
    public async Task ExecuteAsync_AgentsFail_ReturnsEmptyAgentsAndStillSucceeds()
    {
        var action = new WatchWorkspaceAction(
            new GetWorkspaceStatusAction(HttpClient(HttpStatusCode.OK,
                """{"workspaceId":"ws-1","status":"Running","containerCount":0}""")),
            new ListAgentsAction(ThrowingClient(new HttpRequestException("transient"))),
            new ListToolsAction(HttpClient(HttpStatusCode.OK, "[]")));

        var result = await action.ExecuteAsync(new WatchWorkspaceInput("ws-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Agents.ShouldBeEmpty();
        result.Value.Tools.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ToolsFail_ReturnsEmptyToolsAndStillSucceeds()
    {
        var action = new WatchWorkspaceAction(
            new GetWorkspaceStatusAction(HttpClient(HttpStatusCode.OK,
                """{"workspaceId":"ws-1","status":"Running","containerCount":0}""")),
            new ListAgentsAction(HttpClient(HttpStatusCode.OK, "[]")),
            new ListToolsAction(HttpClient(HttpStatusCode.InternalServerError, "")));

        var result = await action.ExecuteAsync(new WatchWorkspaceInput("ws-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Tools.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_StatusUnreachable_PropagatesSiloUnreachable()
    {
        var action = new WatchWorkspaceAction(
            new GetWorkspaceStatusAction(ThrowingClient(new HttpRequestException("connection refused"))),
            new ListAgentsAction(HttpClient(HttpStatusCode.OK, "[]")),
            new ListToolsAction(HttpClient(HttpStatusCode.OK, "[]")));

        var result = await action.ExecuteAsync(new WatchWorkspaceInput("ws-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.SiloUnreachable);
    }

    [Fact]
    public async Task ExecuteAsync_NullInput_Throws()
    {
        var action = new WatchWorkspaceAction(
            new GetWorkspaceStatusAction(HttpClient(HttpStatusCode.OK, "{}")),
            new ListAgentsAction(HttpClient(HttpStatusCode.OK, "[]")),
            new ListToolsAction(HttpClient(HttpStatusCode.OK, "[]")));

        await Should.ThrowAsync<ArgumentNullException>(
            () => action.ExecuteAsync(null!, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteAsync_BlankWorkspaceId_Throws(string id)
    {
        var action = new WatchWorkspaceAction(
            new GetWorkspaceStatusAction(HttpClient(HttpStatusCode.OK, "{}")),
            new ListAgentsAction(HttpClient(HttpStatusCode.OK, "[]")),
            new ListToolsAction(HttpClient(HttpStatusCode.OK, "[]")));

        await Should.ThrowAsync<ArgumentException>(
            () => action.ExecuteAsync(new WatchWorkspaceInput(id), CancellationToken.None));
    }

    private static HttpClient HttpClient(HttpStatusCode status, string body)
        => new(StubHttpMessageHandler.Returns(status, body)) { BaseAddress = new Uri("http://example.test") };

    private static HttpClient ThrowingClient(Exception ex)
        => new(StubHttpMessageHandler.Throws(ex)) { BaseAddress = new Uri("http://example.test") };
}
