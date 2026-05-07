using System.Net;
using Weave.Actions.Context;
using Weave.Actions.Marketplace;
using Weave.Actions.Tests.Helpers;

namespace Weave.Actions.Tests.Marketplace;

public sealed class InstallMarketplaceItemActionTests
{
    private const string SuccessBody = """
    {
      "item": { "itemId": "mp-1", "name": "Alpha", "description": "d", "category": "c", "version": "1", "author": "a", "status": "Published", "installCount": 1, "templateId": "tpl-1" },
      "template": {
        "templateId": "tpl-1", "name": "Alpha", "description": "d", "version": "1", "author": "a", "status": "Published", "instantiationCount": 1,
        "agentDefinition": { "model": "claude", "systemPromptFile": "./prompts/assistant.md", "maxConcurrentTasks": 3 },
        "requiredTools": {}
      }
    }
    """;

    [Fact]
    public async Task ExecuteAsync_Success_ReturnsItemAndTemplate()
    {
        using var client = HttpClientReturning(HttpStatusCode.OK, SuccessBody);
        var action = new InstallMarketplaceItemAction(client);

        var result = await action.ExecuteAsync(new InstallMarketplaceItemInput("mp-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Item.ItemId.ShouldBe("mp-1");
        result.Value.Template.TemplateId.ShouldBe("tpl-1");
        result.Value.Template.AgentDefinition.Model.ShouldBe("claude");
    }

    [Fact]
    public async Task ExecuteAsync_NotFound_ReturnsNotFound()
    {
        using var client = HttpClientReturning(HttpStatusCode.NotFound, string.Empty);
        var action = new InstallMarketplaceItemAction(client);

        var result = await action.ExecuteAsync(new InstallMarketplaceItemInput("missing"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.NotFound);
    }

    [Fact]
    public async Task ExecuteAsync_Conflict_ReturnsConflictWithSiloDetail()
    {
        using var client = HttpClientReturning(HttpStatusCode.Conflict, "Item is not Published.");
        var action = new InstallMarketplaceItemAction(client);

        var result = await action.ExecuteAsync(new InstallMarketplaceItemInput("mp-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.Conflict);
        result.Failure.Message.ShouldBe("Item is not Published.");
    }

    [Fact]
    public async Task ExecuteAsync_TargetsInstallEndpointWithPostMethod()
    {
        var handler = StubHttpMessageHandler.Returns(HttpStatusCode.OK, SuccessBody);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://example.test") };
        var action = new InstallMarketplaceItemAction(client);

        await action.ExecuteAsync(new InstallMarketplaceItemInput("mp-1"), CancellationToken.None);

        handler.LastRequestUri!.AbsolutePath.ShouldBe("/api/marketplace/mp-1/install");
        handler.LastRequestMethod.ShouldBe(HttpMethod.Post);
    }

    [Fact]
    public async Task ExecuteAsync_HttpRequestException_ReturnsSiloUnreachable()
    {
        using var client = new HttpClient(StubHttpMessageHandler.Throws(new HttpRequestException("nope")))
        {
            BaseAddress = new Uri("http://example.test")
        };
        var action = new InstallMarketplaceItemAction(client);

        var result = await action.ExecuteAsync(new InstallMarketplaceItemInput("mp-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.SiloUnreachable);
    }

    private static HttpClient HttpClientReturning(HttpStatusCode status, string body)
        => new(StubHttpMessageHandler.Returns(status, body)) { BaseAddress = new Uri("http://example.test") };
}
