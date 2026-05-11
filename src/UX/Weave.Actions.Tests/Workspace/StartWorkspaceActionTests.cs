using System.Net;
using Weave.Actions.Context;
using Weave.Actions.Tests.Helpers;
using Weave.Actions.Workspace;
using Weave.Workspaces.Manifest;

namespace Weave.Actions.Tests.Workspace;

public sealed class StartWorkspaceActionTests
{
    [Fact]
    public async Task ExecuteAsync_201_ReturnsTranslatedSummary()
    {
        const string body = """
        {
          "workspaceId": "ws-1",
          "name": "demo",
          "status": "Running",
          "containerCount": 3,
          "startedAt": "2026-05-07T10:00:00Z",
          "stoppedAt": null,
          "networkId": "weave-demo",
          "errorMessage": null
        }
        """;
        using var client = HttpClientReturning(HttpStatusCode.Created, body);
        var action = new StartWorkspaceAction(client);

        var result = await action.ExecuteAsync(new StartWorkspaceInput(NewManifest("demo")), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Workspace.WorkspaceId.ShouldBe("ws-1");
        result.Value.Workspace.Name.ShouldBe("demo");
        result.Value.Workspace.Status.ShouldBe("Running");
        result.Value.Workspace.ContainerCount.ShouldBe(3);
        result.Value.Workspace.StartedAt.ShouldBe(new DateTimeOffset(2026, 5, 7, 10, 0, 0, TimeSpan.Zero));
        result.Value.Workspace.NetworkId.ShouldBe("weave-demo");
    }

    [Fact]
    public async Task ExecuteAsync_400_ReturnsValidationFailedWithExtractedMessage()
    {
        const string body = """
        {
          "title": "Validation Failed",
          "errors": {
            "manifest.name": ["Name is required."],
            "manifest.version": ["Version is required."]
          }
        }
        """;
        using var client = HttpClientReturning(HttpStatusCode.BadRequest, body);
        var action = new StartWorkspaceAction(client);

        var result = await action.ExecuteAsync(new StartWorkspaceInput(NewManifest("")), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
        result.Failure.Reason.ShouldBe(ActionFailureReason.ValidationFailed);
        result.Failure.Message.ShouldContain("manifest.name: Name is required.");
        result.Failure.Message.ShouldContain("manifest.version: Version is required.");
    }

    [Fact]
    public async Task ExecuteAsync_400_NoErrorsMap_FallsBackToDefault()
    {
        const string body = """{ "title": "Bad request" }""";
        using var client = HttpClientReturning(HttpStatusCode.BadRequest, body);
        var action = new StartWorkspaceAction(client);

        var result = await action.ExecuteAsync(new StartWorkspaceInput(NewManifest("demo")), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.ValidationFailed);
        result.Failure.Message.ShouldBe("Manifest is invalid.");
    }

    [Fact]
    public async Task ExecuteAsync_409_ReturnsConflictWithSiloDetail()
    {
        // The silo's <<MARKER-FROM-SILO>> token is unique to the stub —
        // ensures we're surfacing the silo's detail rather than coincidentally
        // matching the action's fallback default.
        const string body = """{ "title": "Conflict", "detail": "Workspace is already busy <<MARKER-FROM-SILO>>." }""";
        using var client = HttpClientReturning(HttpStatusCode.Conflict, body);
        var action = new StartWorkspaceAction(client);

        var result = await action.ExecuteAsync(new StartWorkspaceInput(NewManifest("demo")), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.Conflict);
        result.Failure.Message.ShouldContain("<<MARKER-FROM-SILO>>");
    }

    [Fact]
    public async Task ExecuteAsync_409_NoDetail_FallsBackToDefault()
    {
        const string body = """{ "title": "Conflict" }""";
        using var client = HttpClientReturning(HttpStatusCode.Conflict, body);
        var action = new StartWorkspaceAction(client);

        var result = await action.ExecuteAsync(new StartWorkspaceInput(NewManifest("demo")), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.Conflict);
        result.Failure.Message.ShouldContain("already running or in a conflicting state");
    }

    [Fact]
    public async Task ExecuteAsync_401_MapsToUnauthorized()
    {
        using var client = HttpClientReturning(HttpStatusCode.Unauthorized, "");
        var action = new StartWorkspaceAction(client);

        var result = await action.ExecuteAsync(new StartWorkspaceInput(NewManifest("demo")), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.Unauthorized);
    }

    [Fact]
    public async Task ExecuteAsync_500_MapsToInternal()
    {
        using var client = HttpClientReturning(HttpStatusCode.InternalServerError, "");
        var action = new StartWorkspaceAction(client);

        var result = await action.ExecuteAsync(new StartWorkspaceInput(NewManifest("demo")), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.Internal);
    }

    [Fact]
    public async Task ExecuteAsync_HttpRequestException_ReturnsSiloUnreachable()
    {
        using var client = HttpClientThrowing(new HttpRequestException("connection refused"));
        var action = new StartWorkspaceAction(client);

        var result = await action.ExecuteAsync(new StartWorkspaceInput(NewManifest("demo")), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
        result.Failure.Reason.ShouldBe(ActionFailureReason.SiloUnreachable);
        result.Failure.Message.ShouldContain("connection refused");
    }

    [Fact]
    public async Task ExecuteAsync_TargetsWorkspacesEndpoint()
    {
        var handler = StubHttpMessageHandler.Returns(HttpStatusCode.Created,
            """{"workspaceId":"x","status":"Running","containerCount":0}""");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://example.test") };
        var action = new StartWorkspaceAction(client);

        await action.ExecuteAsync(new StartWorkspaceInput(NewManifest("demo")), CancellationToken.None);

        handler.LastRequestUri!.AbsolutePath.ShouldBe("/api/workspaces");
    }

    [Fact]
    public async Task ExecuteAsync_NullInput_Throws()
    {
        using var client = HttpClientReturning(HttpStatusCode.OK, "{}");
        var action = new StartWorkspaceAction(client);

        await Should.ThrowAsync<ArgumentNullException>(
            () => action.ExecuteAsync(null!, CancellationToken.None));
    }

    private static WorkspaceManifest NewManifest(string name) => new()
    {
        Name = name,
        Version = "1.0.0"
    };

    private static HttpClient HttpClientReturning(HttpStatusCode status, string body)
        => new(StubHttpMessageHandler.Returns(status, body)) { BaseAddress = new Uri("http://example.test") };

    private static HttpClient HttpClientThrowing(Exception ex)
        => new(StubHttpMessageHandler.Throws(ex)) { BaseAddress = new Uri("http://example.test") };
}
