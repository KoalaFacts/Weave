using System.Net;
using Microsoft.Extensions.AI;
using Weave.Agents.Pipeline.Providers.Anthropic;

namespace Weave.Agents.Tests.Pipeline.Providers.Anthropic;

public class AnthropicChatClientTests
{
    private const string ValidResponse = """
        {
          "id": "<<MARKER-MSG-ID>>",
          "type": "message",
          "role": "assistant",
          "content": [{"type": "text", "text": "<<MARKER-REPLY>>"}],
          "model": "<<MARKER-MODEL>>",
          "stop_reason": "end_turn",
          "usage": {"input_tokens": 11, "output_tokens": 22}
        }
        """;

    [Fact]
    public async Task GetResponseAsync_PostsToMessagesEndpoint_WithRequiredHeaders()
    {
        var (client, handler) = CreateClient(ValidResponse);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            cancellationToken: TestContext.Current.CancellationToken);

        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest.Method.ShouldBe(HttpMethod.Post);
        handler.LastRequest.RequestUri!.ToString().ShouldBe("https://api.anthropic.com/v1/messages");
        handler.LastRequest.Headers.GetValues("x-api-key").ShouldContain("sk-ant-test");
        handler.LastRequest.Headers.GetValues("anthropic-version").ShouldContain("2023-06-01");
        handler.LastRequest.Content!.Headers.ContentType!.MediaType.ShouldBe("application/json");
    }

    [Fact]
    public async Task GetResponseAsync_SerializesModelAndMessages_AsSnakeCase()
    {
        var (client, handler) = CreateClient(ValidResponse);

        await client.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, "be terse"),
                new ChatMessage(ChatRole.User, "hi there")
            ],
            cancellationToken: TestContext.Current.CancellationToken);

        handler.LastRequestBody.ShouldNotBeNull();
        handler.LastRequestBody.ShouldContain("\"model\":\"claude-sonnet-4-20250514\"");
        handler.LastRequestBody.ShouldContain("\"max_tokens\":");
        handler.LastRequestBody.ShouldContain("\"system\":\"be terse\"");
        handler.LastRequestBody.ShouldContain("\"role\":\"user\"");
        handler.LastRequestBody.ShouldContain("\"content\":\"hi there\"");
    }

    [Fact]
    public async Task GetResponseAsync_CombinesMultipleSystemMessages_WithNewlineSeparator()
    {
        var (client, handler) = CreateClient(ValidResponse);

        await client.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, "rule one"),
                new ChatMessage(ChatRole.System, "rule two"),
                new ChatMessage(ChatRole.User, "ok")
            ],
            cancellationToken: TestContext.Current.CancellationToken);

        handler.LastRequestBody.ShouldNotBeNull();
        handler.LastRequestBody.ShouldContain("\"system\":\"rule one\\nrule two\"");
    }

    [Fact]
    public async Task GetResponseAsync_TranslatesResponse_ToChatResponse()
    {
        var (client, _) = CreateClient(ValidResponse);

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: TestContext.Current.CancellationToken);

        response.ResponseId.ShouldBe("<<MARKER-MSG-ID>>");
        response.Text.ShouldBe("<<MARKER-REPLY>>");
        response.ModelId.ShouldBe("<<MARKER-MODEL>>");
        response.FinishReason.ShouldBe(ChatFinishReason.Stop);
        response.Usage!.InputTokenCount.ShouldBe(11);
        response.Usage.OutputTokenCount.ShouldBe(22);
    }

    [Theory]
    [InlineData("end_turn", "stop")]
    [InlineData("max_tokens", "length")]
    [InlineData("stop_sequence", "stop")]
    [InlineData("tool_use", "tool_calls")]
    public async Task GetResponseAsync_MapsStopReason_ToFinishReason(string stopReason, string expectedFinish)
    {
        var body = "{\"id\":\"x\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"x\"}],\"model\":\"m\",\"stop_reason\":\""
            + stopReason
            + "\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}";
        var (client, _) = CreateClient(body);

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "x")],
            cancellationToken: TestContext.Current.CancellationToken);

        response.FinishReason!.Value.ToString().ShouldBe(expectedFinish);
    }

    [Fact]
    public async Task GetResponseAsync_OnHttpError_ThrowsHttpRequestException()
    {
        var (client, _) = CreateClient("{\"error\":\"unauthorized\"}", HttpStatusCode.Unauthorized);

        await Should.ThrowAsync<HttpRequestException>(() =>
            client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "x")],
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void GetService_ChatClientMetadata_ReturnsAnthropicProvider()
    {
        var (client, _) = CreateClient(ValidResponse);

        var metadata = client.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata;

        metadata.ShouldNotBeNull();
        metadata.ProviderName.ShouldBe("anthropic");
        metadata.DefaultModelId.ShouldBe("claude-sonnet-4-20250514");
    }

    [Fact]
    public async Task GetResponseAsync_NullMessages_Throws()
    {
        var (client, _) = CreateClient(ValidResponse);

        await Should.ThrowAsync<ArgumentNullException>(() =>
            client.GetResponseAsync(null!, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetResponseAsync_OptionsModelId_OverridesDefault_InRequestBody()
    {
        var (client, handler) = CreateClient(ValidResponse);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { ModelId = "claude-opus-4-override" },
            TestContext.Current.CancellationToken);

        handler.LastRequestBody!.ShouldContain("\"model\":\"claude-opus-4-override\"");
    }

    private static (AnthropicChatClient client, StubHttpMessageHandler handler) CreateClient(
        string body,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new StubHttpMessageHandler().Returns(status, body);
        var http = new HttpClient(handler);
        var client = new AnthropicChatClient(http, "claude-sonnet-4-20250514", "sk-ant-test");
        return (client, handler);
    }
}
