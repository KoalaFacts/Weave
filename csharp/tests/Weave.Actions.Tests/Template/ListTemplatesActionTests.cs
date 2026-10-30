using System.Net;
using Weave.Actions.Context;
using Weave.Actions.Template;
using Weave.Actions.Tests.Helpers;

namespace Weave.Actions.Tests.Template;

public sealed class ListTemplatesActionTests
{
    [Fact]
    public async Task ExecuteAsync_LiveTemplates_ReturnsOpaqueElements()
    {
        const string body = """[{"templateId":"tpl-1"},{"templateId":"tpl-2"}]""";
        using var client = HttpClientReturning(HttpStatusCode.OK, body);
        var action = new ListTemplatesAction(client);

        var result = await action.ExecuteAsync(new ListTemplatesInput(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Templates.Count.ShouldBe(2);
        result.Value.Templates[0].GetProperty("templateId").GetString().ShouldBe("tpl-1");
    }

    [Fact]
    public async Task ExecuteAsync_HttpRequestException_ReturnsSiloUnreachable()
    {
        using var client = new HttpClient(StubHttpMessageHandler.Throws(new HttpRequestException("nope")))
        {
            BaseAddress = new Uri("http://example.test")
        };
        var action = new ListTemplatesAction(client);

        var result = await action.ExecuteAsync(new ListTemplatesInput(), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.SiloUnreachable);
    }

    [Fact]
    public async Task ExecuteAsync_TargetsTemplatesEndpoint()
    {
        var handler = StubHttpMessageHandler.Returns(HttpStatusCode.OK, "[]");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://example.test") };
        var action = new ListTemplatesAction(client);

        await action.ExecuteAsync(new ListTemplatesInput(), CancellationToken.None);

        handler.LastRequestUri!.AbsolutePath.ShouldBe("/api/templates");
    }

    private static HttpClient HttpClientReturning(HttpStatusCode status, string body)
        => new(StubHttpMessageHandler.Returns(status, body)) { BaseAddress = new Uri("http://example.test") };
}
