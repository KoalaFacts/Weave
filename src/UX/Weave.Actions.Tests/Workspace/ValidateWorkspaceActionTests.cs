using System.Net;
using Weave.Actions.Context;
using Weave.Actions.Tests.Helpers;
using Weave.Actions.Workspace;

namespace Weave.Actions.Tests.Workspace;

public sealed class ValidateWorkspaceActionTests
{
    [Fact]
    public async Task ExecuteAsync_ValidManifest_ReturnsSummaryWithEmptyErrors()
    {
        const string siloResponse = """
        {
          "name": "demo",
          "agentCount": 1,
          "toolCount": 1,
          "targetCount": 1,
          "errors": []
        }
        """;
        using var temp = TempFile.With("{ \"name\": \"demo\" }");
        using var client = HttpClientReturning(HttpStatusCode.OK, siloResponse);
        var action = new ValidateWorkspaceAction(client);

        var result = await action.ExecuteAsync(new ValidateWorkspaceInput(temp.Path), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Name.ShouldBe("demo");
        result.Value.AgentCount.ShouldBe(1);
        result.Value.ToolCount.ShouldBe(1);
        result.Value.TargetCount.ShouldBe(1);
        result.Value.IsValid.ShouldBeTrue();
        result.Value.Errors.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_StructurallyInvalidManifest_ReturnsErrorsInResult()
    {
        const string siloResponse = """
        {
          "name": "bad",
          "agentCount": 0,
          "toolCount": 0,
          "targetCount": 0,
          "errors": ["agent 'alpha' references unknown tool 'git'"]
        }
        """;
        using var temp = TempFile.With("{ \"name\": \"bad\" }");
        using var client = HttpClientReturning(HttpStatusCode.OK, siloResponse);
        var action = new ValidateWorkspaceAction(client);

        var result = await action.ExecuteAsync(new ValidateWorkspaceInput(temp.Path), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.IsValid.ShouldBeFalse();
        result.Value.Errors.Count.ShouldBe(1);
        result.Value.Errors[0].ShouldContain("unknown tool");
    }

    [Fact]
    public async Task ExecuteAsync_MissingFile_ReturnsNotFound()
    {
        var path = Path.Combine(Path.GetTempPath(), $"weave-missing-{Guid.NewGuid():N}.json");
        using var client = HttpClientReturning(HttpStatusCode.OK, "{}");
        var action = new ValidateWorkspaceAction(client);

        var result = await action.ExecuteAsync(new ValidateWorkspaceInput(path), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
        result.Failure.Reason.ShouldBe(ActionFailureReason.NotFound);
    }

    [Fact]
    public async Task ExecuteAsync_SiloReturns400_MapsToValidationFailed()
    {
        const string problem = """
        {
          "title": "One or more validation errors occurred.",
          "errors": { "manifestJson": ["Manifest is not valid JSON: Expected `:`"] }
        }
        """;
        using var temp = TempFile.With("{ this is not json");
        using var client = HttpClientReturning(HttpStatusCode.BadRequest, problem);
        var action = new ValidateWorkspaceAction(client);

        var result = await action.ExecuteAsync(new ValidateWorkspaceInput(temp.Path), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
        result.Failure.Reason.ShouldBe(ActionFailureReason.ValidationFailed);
        result.Failure.Message.ShouldContain("not valid JSON");
    }

    [Fact]
    public async Task ExecuteAsync_HttpRequestException_ReturnsSiloUnreachable()
    {
        using var temp = TempFile.With("{ \"name\": \"demo\" }");
        using var client = HttpClientThrowing(new HttpRequestException("connection refused"));
        var action = new ValidateWorkspaceAction(client);

        var result = await action.ExecuteAsync(new ValidateWorkspaceInput(temp.Path), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
        result.Failure.Reason.ShouldBe(ActionFailureReason.SiloUnreachable);
    }

    [Fact]
    public async Task ExecuteAsync_TargetsValidateEndpoint()
    {
        var handler = StubHttpMessageHandler.Returns(HttpStatusCode.OK,
            """{"name":"demo","agentCount":0,"toolCount":0,"targetCount":0,"errors":[]}""");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://example.test") };
        using var temp = TempFile.With("{ \"name\": \"demo\" }");
        var action = new ValidateWorkspaceAction(client);

        await action.ExecuteAsync(new ValidateWorkspaceInput(temp.Path), CancellationToken.None);

        handler.LastRequestUri!.AbsolutePath.ShouldBe("/api/workspaces/validate");
    }

    [Fact]
    public async Task ExecuteAsync_NullInput_Throws()
    {
        using var client = HttpClientReturning(HttpStatusCode.OK, "{}");
        var action = new ValidateWorkspaceAction(client);

        await Should.ThrowAsync<ArgumentNullException>(
            () => action.ExecuteAsync(null!, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteAsync_BlankPath_Throws(string path)
    {
        using var client = HttpClientReturning(HttpStatusCode.OK, "{}");
        var action = new ValidateWorkspaceAction(client);

        await Should.ThrowAsync<ArgumentException>(
            () => action.ExecuteAsync(new ValidateWorkspaceInput(path), CancellationToken.None));
    }

    private static HttpClient HttpClientReturning(HttpStatusCode status, string body)
        => new(StubHttpMessageHandler.Returns(status, body)) { BaseAddress = new Uri("http://example.test") };

    private static HttpClient HttpClientThrowing(Exception ex)
        => new(StubHttpMessageHandler.Throws(ex)) { BaseAddress = new Uri("http://example.test") };

    /// <summary>
    /// Disposable temp file for tests that need a real on-disk manifest.
    /// </summary>
    private sealed class TempFile : IDisposable
    {
        public string Path { get; }

        private TempFile(string path) { Path = path; }

        public static TempFile With(string content)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"weave-test-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, content);
            return new TempFile(path);
        }

        public void Dispose()
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
    }
}
