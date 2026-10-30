using System.Net;
using Weave.Actions.Context;
using Weave.Actions.Skill;
using Weave.Actions.Tests.Helpers;

namespace Weave.Actions.Tests.Skill;

public sealed class ListSkillsActionTests
{
    [Fact]
    public async Task ExecuteAsync_LiveSkills_ReturnsOpaqueElements()
    {
        const string body = """
        [
          { "name": "alpha", "content": "..." },
          { "name": "beta", "content": "..." }
        ]
        """;
        using var client = HttpClientReturning(HttpStatusCode.OK, body);
        var action = new ListSkillsAction(client);

        var result = await action.ExecuteAsync(new ListSkillsInput("ws-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Skills.Count.ShouldBe(2);
        result.Value.Skills[0].GetProperty("name").GetString().ShouldBe("alpha");
        result.Value.Skills[1].GetProperty("name").GetString().ShouldBe("beta");
    }

    [Fact]
    public async Task ExecuteAsync_EmptyArray_ReturnsSuccessWithEmptyList()
    {
        using var client = HttpClientReturning(HttpStatusCode.OK, "[]");
        var action = new ListSkillsAction(client);

        var result = await action.ExecuteAsync(new ListSkillsInput("ws-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Skills.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_HttpRequestException_ReturnsSiloUnreachable()
    {
        using var client = HttpClientThrowing(new HttpRequestException("connection refused"));
        var action = new ListSkillsAction(client);

        var result = await action.ExecuteAsync(new ListSkillsInput("ws-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.SiloUnreachable);
        result.Failure.Message.ShouldContain("connection refused");
    }

    [Fact]
    public async Task ExecuteAsync_TargetsSkillsEndpointWithEscapedWorkspaceId()
    {
        var handler = StubHttpMessageHandler.Returns(HttpStatusCode.OK, "[]");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://example.test") };
        var action = new ListSkillsAction(client);

        await action.ExecuteAsync(new ListSkillsInput("ws/with spaces"), CancellationToken.None);

        handler.LastRequestUri!.AbsolutePath.ShouldBe("/api/workspaces/ws%2Fwith%20spaces/skills");
    }

    [Fact]
    public async Task ExecuteAsync_NullInput_Throws()
    {
        using var client = HttpClientReturning(HttpStatusCode.OK, "[]");
        var action = new ListSkillsAction(client);

        await Should.ThrowAsync<ArgumentNullException>(
            () => action.ExecuteAsync(null!, CancellationToken.None));
    }

    private static HttpClient HttpClientReturning(HttpStatusCode status, string body)
        => new(StubHttpMessageHandler.Returns(status, body)) { BaseAddress = new Uri("http://example.test") };

    private static HttpClient HttpClientThrowing(Exception ex)
        => new(StubHttpMessageHandler.Throws(ex)) { BaseAddress = new Uri("http://example.test") };
}
