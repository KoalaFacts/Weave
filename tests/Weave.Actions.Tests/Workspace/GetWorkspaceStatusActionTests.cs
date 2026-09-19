using System.Net;
using Weave.Actions.Context;
using Weave.Actions.Tests.Helpers;
using Weave.Actions.Workspace;

namespace Weave.Actions.Tests.Workspace;

public sealed class GetWorkspaceStatusActionTests
{
    [Fact]
    public async Task ExecuteAsync_LiveWorkspace_ReturnsTranslatedSummary()
    {
        const string body = """
        {
          "workspaceId": "ws-1",
          "name": "demo",
          "status": "Running",
          "containerCount": 3,
          "startedAt": "2026-05-06T10:00:00Z",
          "stoppedAt": null,
          "networkId": "weave-demo",
          "errorMessage": null
        }
        """;
        using var client = HttpClientReturning(HttpStatusCode.OK, body);
        var action = new GetWorkspaceStatusAction(client);

        var result = await action.ExecuteAsync(new GetWorkspaceStatusInput("ws-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Workspace.WorkspaceId.ShouldBe("ws-1");
        result.Value.Workspace.Name.ShouldBe("demo");
        result.Value.Workspace.Status.ShouldBe("Running");
        result.Value.Workspace.ContainerCount.ShouldBe(3);
        result.Value.Workspace.StartedAt.ShouldBe(new DateTimeOffset(2026, 5, 6, 10, 0, 0, TimeSpan.Zero));
        result.Value.Workspace.NetworkId.ShouldBe("weave-demo");
    }

    [Fact]
    public async Task ExecuteAsync_404_ReturnsNotFound()
    {
        using var client = HttpClientReturning(HttpStatusCode.NotFound, "{}");
        var action = new GetWorkspaceStatusAction(client);

        var result = await action.ExecuteAsync(new GetWorkspaceStatusInput("ws-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
        result.Failure.Reason.ShouldBe(ActionFailureReason.NotFound);
        result.Failure.Message.ShouldContain("ws-1");
    }

    [Fact]
    public async Task ExecuteAsync_HttpRequestException_ReturnsSiloUnreachable()
    {
        using var client = HttpClientThrowing(new HttpRequestException("connection refused"));
        var action = new GetWorkspaceStatusAction(client);

        var result = await action.ExecuteAsync(new GetWorkspaceStatusInput("ws-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
        result.Failure.Reason.ShouldBe(ActionFailureReason.SiloUnreachable);
        result.Failure.Message.ShouldContain("connection refused");
    }

    [Fact]
    public async Task ExecuteAsync_TargetsWorkspaceEndpointWithEscapedId()
    {
        var handler = StubHttpMessageHandler.Returns(HttpStatusCode.OK,
            """{"workspaceId":"x","status":"Running","containerCount":0}""");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://example.test") };
        var action = new GetWorkspaceStatusAction(client);

        await action.ExecuteAsync(new GetWorkspaceStatusInput("ws/with spaces"), CancellationToken.None);

        handler.LastRequestUri!.AbsolutePath.ShouldBe("/api/workspaces/ws%2Fwith%20spaces");
    }

    [Fact]
    public async Task ExecuteAsync_ServerError_PropagatesAsHttpRequestException()
    {
        using var client = HttpClientReturning(HttpStatusCode.InternalServerError, "{}");
        var action = new GetWorkspaceStatusAction(client);

        var result = await action.ExecuteAsync(new GetWorkspaceStatusInput("ws-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
        result.Failure.Reason.ShouldBe(ActionFailureReason.SiloUnreachable);
    }

    [Fact]
    public async Task ExecuteAsync_NullInput_Throws()
    {
        using var client = HttpClientReturning(HttpStatusCode.OK, "{}");
        var action = new GetWorkspaceStatusAction(client);

        await Should.ThrowAsync<ArgumentNullException>(
            () => action.ExecuteAsync(null!, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteAsync_BlankWorkspaceId_Throws(string workspaceId)
    {
        using var client = HttpClientReturning(HttpStatusCode.OK, "{}");
        var action = new GetWorkspaceStatusAction(client);

        await Should.ThrowAsync<ArgumentException>(
            () => action.ExecuteAsync(new GetWorkspaceStatusInput(workspaceId), CancellationToken.None));
    }

    private static HttpClient HttpClientReturning(HttpStatusCode status, string body)
        => new(StubHttpMessageHandler.Returns(status, body)) { BaseAddress = new Uri("http://example.test") };

    private static HttpClient HttpClientThrowing(Exception ex)
        => new(StubHttpMessageHandler.Throws(ex)) { BaseAddress = new Uri("http://example.test") };
}
