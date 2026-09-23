using System.Net;
using Weave.Actions.Context;
using Weave.Actions.Tests.Helpers;
using Weave.Actions.Workspace;

namespace Weave.Actions.Tests.Workspace;

public sealed class StopWorkspaceActionTests
{
    [Fact]
    public async Task ExecuteAsync_204_ReturnsSuccess()
    {
        using var client = HttpClientReturning(HttpStatusCode.NoContent, body: null);
        var action = new StopWorkspaceAction(client);

        var result = await action.ExecuteAsync(new StopWorkspaceInput("ws-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_409_ReturnsConflictWithSiloDetail()
    {
        const string body = """{ "title": "Conflict", "detail": "Workspace is currently stopping." }""";
        using var client = HttpClientReturning(HttpStatusCode.Conflict, body);
        var action = new StopWorkspaceAction(client);

        var result = await action.ExecuteAsync(new StopWorkspaceInput("ws-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.Conflict);
        result.Failure.Message.ShouldContain("currently stopping");
    }

    [Fact]
    public async Task ExecuteAsync_409_NoDetail_FallsBackToDefault()
    {
        const string body = """{ "title": "Conflict" }""";
        using var client = HttpClientReturning(HttpStatusCode.Conflict, body);
        var action = new StopWorkspaceAction(client);

        var result = await action.ExecuteAsync(new StopWorkspaceInput("ws-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.Conflict);
        result.Failure.Message.ShouldContain("ws-1");
        result.Failure.Message.ShouldContain("could not be stopped");
    }

    [Fact]
    public async Task ExecuteAsync_401_MapsToUnauthorized()
    {
        using var client = HttpClientReturning(HttpStatusCode.Unauthorized, "");
        var action = new StopWorkspaceAction(client);

        var result = await action.ExecuteAsync(new StopWorkspaceInput("ws-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.Unauthorized);
    }

    [Fact]
    public async Task ExecuteAsync_500_MapsToInternal()
    {
        using var client = HttpClientReturning(HttpStatusCode.InternalServerError, "");
        var action = new StopWorkspaceAction(client);

        var result = await action.ExecuteAsync(new StopWorkspaceInput("ws-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.Internal);
    }

    [Fact]
    public async Task ExecuteAsync_HttpRequestException_ReturnsSiloUnreachable()
    {
        using var client = HttpClientThrowing(new HttpRequestException("connection refused"));
        var action = new StopWorkspaceAction(client);

        var result = await action.ExecuteAsync(new StopWorkspaceInput("ws-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
        result.Failure.Reason.ShouldBe(ActionFailureReason.SiloUnreachable);
        result.Failure.Message.ShouldContain("connection refused");
    }

    [Fact]
    public async Task ExecuteAsync_TargetsWorkspaceEndpointWithEscapedId()
    {
        var handler = StubHttpMessageHandler.Returns(HttpStatusCode.NoContent, body: null);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://example.test") };
        var action = new StopWorkspaceAction(client);

        await action.ExecuteAsync(new StopWorkspaceInput("ws/with spaces"), CancellationToken.None);

        handler.LastRequestUri!.AbsolutePath.ShouldBe("/api/workspaces/ws%2Fwith%20spaces");
    }

    [Fact]
    public async Task ExecuteAsync_UsesDeleteVerb()
    {
        var handler = StubHttpMessageHandler.Returns(HttpStatusCode.NoContent, body: null);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://example.test") };
        var action = new StopWorkspaceAction(client);

        await action.ExecuteAsync(new StopWorkspaceInput("ws-1"), CancellationToken.None);

        handler.LastRequestMethod.ShouldBe(HttpMethod.Delete);
    }

    [Fact]
    public async Task ExecuteAsync_NullInput_Throws()
    {
        using var client = HttpClientReturning(HttpStatusCode.OK, "{}");
        var action = new StopWorkspaceAction(client);

        await Should.ThrowAsync<ArgumentNullException>(
            () => action.ExecuteAsync(null!, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteAsync_BlankWorkspaceId_Throws(string workspaceId)
    {
        using var client = HttpClientReturning(HttpStatusCode.OK, "{}");
        var action = new StopWorkspaceAction(client);

        await Should.ThrowAsync<ArgumentException>(
            () => action.ExecuteAsync(new StopWorkspaceInput(workspaceId), CancellationToken.None));
    }

    private static HttpClient HttpClientReturning(HttpStatusCode status, string? body)
        => new(StubHttpMessageHandler.Returns(status, body)) { BaseAddress = new Uri("http://example.test") };

    private static HttpClient HttpClientThrowing(Exception ex)
        => new(StubHttpMessageHandler.Throws(ex)) { BaseAddress = new Uri("http://example.test") };
}
