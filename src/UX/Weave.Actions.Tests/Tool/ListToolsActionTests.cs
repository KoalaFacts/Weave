using System.Net;
using Weave.Actions.Context;
using Weave.Actions.Tests.Helpers;
using Weave.Actions.Tool;

namespace Weave.Actions.Tests.Tool;

public sealed class ListToolsActionTests
{
    [Fact]
    public async Task ExecuteAsync_LiveTools_ReturnsTranslatedSummaries()
    {
        const string body = """
        [
          { "toolName": "git", "toolType": "Cli", "status": "Connected", "endpoint": "/usr/bin/git", "connectedAt": "2026-05-06T10:00:00Z", "errorMessage": null },
          { "toolName": "files", "toolType": "FileSystem", "status": "Disconnected", "endpoint": null, "connectedAt": null, "errorMessage": "denied" }
        ]
        """;
        using var client = HttpClientReturning(HttpStatusCode.OK, body);
        var action = new ListToolsAction(client);

        var result = await action.ExecuteAsync(new ListToolsInput("ws-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Tools.Count.ShouldBe(2);
        result.Value.Tools[0].ToolName.ShouldBe("git");
        result.Value.Tools[0].ToolType.ShouldBe("Cli");
        result.Value.Tools[0].Status.ShouldBe("Connected");
        result.Value.Tools[0].Endpoint.ShouldBe("/usr/bin/git");
        result.Value.Tools[0].ConnectedAt.ShouldNotBeNull();
        result.Value.Tools[1].ToolName.ShouldBe("files");
        result.Value.Tools[1].Endpoint.ShouldBeNull();
        result.Value.Tools[1].ConnectedAt.ShouldBeNull();
        result.Value.Tools[1].ErrorMessage.ShouldBe("denied");
    }

    [Fact]
    public async Task ExecuteAsync_EmptyArray_ReturnsSuccessWithEmptyList()
    {
        using var client = HttpClientReturning(HttpStatusCode.OK, "[]");
        var action = new ListToolsAction(client);

        var result = await action.ExecuteAsync(new ListToolsInput("ws-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Tools.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_HttpRequestException_ReturnsSiloUnreachable()
    {
        using var client = HttpClientThrowing(new HttpRequestException("connection refused"));
        var action = new ListToolsAction(client);

        var result = await action.ExecuteAsync(new ListToolsInput("ws-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
        result.Failure.Reason.ShouldBe(ActionFailureReason.SiloUnreachable);
        result.Failure.Message.ShouldContain("connection refused");
    }

    [Fact]
    public async Task ExecuteAsync_TargetsToolsEndpointWithEscapedWorkspaceId()
    {
        var handler = StubHttpMessageHandler.Returns(HttpStatusCode.OK, "[]");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://example.test") };
        var action = new ListToolsAction(client);

        await action.ExecuteAsync(new ListToolsInput("ws/with spaces"), CancellationToken.None);

        handler.LastRequestUri!.AbsolutePath.ShouldBe("/api/workspaces/ws%2Fwith%20spaces/tools");
    }

    [Fact]
    public async Task ExecuteAsync_NullInput_Throws()
    {
        using var client = HttpClientReturning(HttpStatusCode.OK, "[]");
        var action = new ListToolsAction(client);

        await Should.ThrowAsync<ArgumentNullException>(
            () => action.ExecuteAsync(null!, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteAsync_BlankWorkspaceId_Throws(string workspaceId)
    {
        using var client = HttpClientReturning(HttpStatusCode.OK, "[]");
        var action = new ListToolsAction(client);

        await Should.ThrowAsync<ArgumentException>(
            () => action.ExecuteAsync(new ListToolsInput(workspaceId), CancellationToken.None));
    }

    private static HttpClient HttpClientReturning(HttpStatusCode status, string body)
        => new(StubHttpMessageHandler.Returns(status, body)) { BaseAddress = new Uri("http://example.test") };

    private static HttpClient HttpClientThrowing(Exception ex)
        => new(StubHttpMessageHandler.Throws(ex)) { BaseAddress = new Uri("http://example.test") };
}
