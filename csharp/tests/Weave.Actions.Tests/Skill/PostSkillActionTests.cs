using System.Net;
using System.Text.Json;
using Weave.Actions.Context;
using Weave.Actions.Skill;
using Weave.Actions.Tests.Helpers;

namespace Weave.Actions.Tests.Skill;

public sealed class PostSkillActionTests
{
    private static readonly JsonElement SampleSkill =
        JsonSerializer.Deserialize<JsonElement>("""{"name":"sample","content":"x"}""");

    [Fact]
    public async Task ExecuteAsync_SuccessStatus_ReturnsSuccess()
    {
        using var client = HttpClientReturning(HttpStatusCode.NoContent);
        var action = new PostSkillAction(client);

        var result = await action.ExecuteAsync(new PostSkillInput("ws-1", SampleSkill), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_BadRequest_ReturnsValidationFailed()
    {
        using var client = HttpClientReturning(HttpStatusCode.BadRequest);
        var action = new PostSkillAction(client);

        var result = await action.ExecuteAsync(new PostSkillInput("ws-1", SampleSkill), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.ValidationFailed);
    }

    [Fact]
    public async Task ExecuteAsync_Unauthorized_ReturnsUnauthorized()
    {
        using var client = HttpClientReturning(HttpStatusCode.Unauthorized);
        var action = new PostSkillAction(client);

        var result = await action.ExecuteAsync(new PostSkillInput("ws-1", SampleSkill), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.Unauthorized);
    }

    [Fact]
    public async Task ExecuteAsync_HttpRequestException_ReturnsSiloUnreachable()
    {
        using var client = HttpClientThrowing(new HttpRequestException("connection refused"));
        var action = new PostSkillAction(client);

        var result = await action.ExecuteAsync(new PostSkillInput("ws-1", SampleSkill), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.SiloUnreachable);
    }

    [Fact]
    public async Task ExecuteAsync_TargetsSkillsEndpointWithPostMethod()
    {
        var handler = StubHttpMessageHandler.Returns(HttpStatusCode.NoContent);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://example.test") };
        var action = new PostSkillAction(client);

        await action.ExecuteAsync(new PostSkillInput("ws-1", SampleSkill), CancellationToken.None);

        handler.LastRequestUri!.AbsolutePath.ShouldBe("/api/workspaces/ws-1/skills");
        handler.LastRequestMethod.ShouldBe(HttpMethod.Post);
    }

    private static HttpClient HttpClientReturning(HttpStatusCode status)
        => new(StubHttpMessageHandler.Returns(status)) { BaseAddress = new Uri("http://example.test") };

    private static HttpClient HttpClientThrowing(Exception ex)
        => new(StubHttpMessageHandler.Throws(ex)) { BaseAddress = new Uri("http://example.test") };
}
