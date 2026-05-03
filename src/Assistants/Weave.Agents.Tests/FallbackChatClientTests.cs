using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Agents.Pipeline;

namespace Weave.Agents.Tests;

public sealed class FallbackChatClientTests : IDisposable
{
    private readonly FallbackChatClient _client = new("test-model", NullLogger<FallbackChatClient>.Instance);

    public void Dispose() => _client.Dispose();

    private static StubTool CreateTestTool(string name) => new(name);

    private sealed class StubTool(string name) : AITool
    {
        public override string Name => name;
    }

    // --- GetResponseAsync: basic behavior ---

    [Fact]
    public async Task GetResponseAsync_PlainMessage_EchoesWithModelPrefix()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello world")
        };

        var response = await _client.GetResponseAsync(messages, cancellationToken: TestContext.Current.CancellationToken);

        response.Messages.ShouldHaveSingleItem();
        response.Messages[0].Role.ShouldBe(ChatRole.Assistant);
        response.Messages[0].Text.ShouldContain("Hello world");
        response.ModelId.ShouldBe("test-model");
    }

    [Fact]
    public async Task GetResponseAsync_EmptyMessages_ReturnsNoInputProvided()
    {
        var messages = new List<ChatMessage>();

        var response = await _client.GetResponseAsync(messages, cancellationToken: TestContext.Current.CancellationToken);

        response.Messages[0].Text.ShouldBe("No input provided.");
    }

    [Fact]
    public async Task GetResponseAsync_WhitespaceMessage_ReturnsNoInputProvided()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "   ")
        };

        var response = await _client.GetResponseAsync(messages, cancellationToken: TestContext.Current.CancellationToken);

        response.Messages[0].Text.ShouldBe("No input provided.");
    }

    [Fact]
    public async Task GetResponseAsync_OptionsOverridesModelId()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "test") };
        var options = new ChatOptions { ModelId = "custom-model" };

        var response = await _client.GetResponseAsync(messages, options, TestContext.Current.CancellationToken);

        response.ModelId.ShouldBe("custom-model");
        response.Messages[0].Text.ShouldContain("[custom-model]");
    }

    [Fact]
    public async Task GetResponseAsync_NullMessages_Throws()
    {
        await Should.ThrowAsync<ArgumentNullException>(
            () => _client.GetResponseAsync(null!, cancellationToken: TestContext.Current.CancellationToken));
    }

    // --- GetResponseAsync: tool call parsing ---

    [Fact]
    public async Task GetResponseAsync_ToolCommand_CreatesFunctionCall()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "/tool my-tool some input") };
        var options = new ChatOptions { Tools = [CreateTestTool("my-tool")] };

        var response = await _client.GetResponseAsync(messages, options, TestContext.Current.CancellationToken);

        var functionCall = response.Messages[0].Contents.OfType<FunctionCallContent>().FirstOrDefault();
        functionCall.ShouldNotBeNull();
        functionCall!.Name.ShouldBe("my-tool");
    }

    [Fact]
    public async Task GetResponseAsync_ToolCommand_UnavailableTool_ReturnsNotAvailable()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "/tool nonexistent some input") };
        var options = new ChatOptions { Tools = [] };

        var response = await _client.GetResponseAsync(messages, options, TestContext.Current.CancellationToken);

        response.Messages[0].Text.ShouldContain("not available");
    }

    [Fact]
    public async Task GetResponseAsync_ToolCommand_NoToolsInOptions_ReturnsNotAvailable()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "/tool my-tool input") };

        var response = await _client.GetResponseAsync(messages, cancellationToken: TestContext.Current.CancellationToken);

        response.Messages[0].Text.ShouldContain("not available");
    }

    [Fact]
    public async Task GetResponseAsync_ToolCommand_ToolNameOnly_CreatesFunctionCall()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "/tool my-tool") };
        var options = new ChatOptions { Tools = [CreateTestTool("my-tool")] };

        var response = await _client.GetResponseAsync(messages, options, TestContext.Current.CancellationToken);

        var functionCall = response.Messages[0].Contents.OfType<FunctionCallContent>().FirstOrDefault();
        functionCall.ShouldNotBeNull();
        functionCall!.Name.ShouldBe("my-tool");
    }

    [Fact]
    public async Task GetResponseAsync_ToolCommand_JsonInput_ParsesStructured()
    {
        var jsonInput = "{\"method\": \"fetch\", \"url\": \"http://example.com\"}";
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "/tool my-tool " + jsonInput)
        };
        var options = new ChatOptions { Tools = [CreateTestTool("my-tool")] };

        var response = await _client.GetResponseAsync(messages, options, TestContext.Current.CancellationToken);

        var functionCall = response.Messages[0].Contents.OfType<FunctionCallContent>().FirstOrDefault();
        functionCall.ShouldNotBeNull();
        functionCall!.Arguments.ShouldNotBeNull();
        functionCall.Arguments!["url"].ShouldBe("http://example.com");
    }

    [Fact]
    public async Task GetResponseAsync_ToolCommand_MalformedJson_FallsToPlainInput()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "/tool my-tool {not valid json") };
        var options = new ChatOptions { Tools = [CreateTestTool("my-tool")] };

        var response = await _client.GetResponseAsync(messages, options, TestContext.Current.CancellationToken);

        var functionCall = response.Messages[0].Contents.OfType<FunctionCallContent>().FirstOrDefault();
        functionCall.ShouldNotBeNull();
        functionCall!.Arguments.ShouldNotBeNull();
        functionCall.Arguments!["input"].ShouldBe("{not valid json");
    }

    // --- GetResponseAsync: function result handling ---

    [Fact]
    public async Task GetResponseAsync_WithFunctionResult_ReturnsFunctionResultText()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Call a tool"),
            new(ChatRole.Tool, [new FunctionResultContent("call-id", "tool output data")])
        };

        var response = await _client.GetResponseAsync(messages, cancellationToken: TestContext.Current.CancellationToken);

        response.Messages[0].Text.ShouldContain("tool output data");
    }

    // --- GetResponseAsync: usage tracking ---

    [Fact]
    public async Task GetResponseAsync_ReturnsUsageDetails()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "one two three")
        };

        var response = await _client.GetResponseAsync(messages, cancellationToken: TestContext.Current.CancellationToken);

        response.Usage.ShouldNotBeNull();
        response.Usage!.InputTokenCount.ShouldNotBeNull();
        response.Usage.InputTokenCount!.Value.ShouldBeGreaterThan(0);
        response.Usage.OutputTokenCount.ShouldNotBeNull();
        response.Usage.OutputTokenCount!.Value.ShouldBeGreaterThan(0);
    }

    // --- GetStreamingResponseAsync ---

    [Fact]
    public async Task GetStreamingResponseAsync_ReturnsEmpty()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "test") };

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in _client.GetStreamingResponseAsync(messages, cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        updates.ShouldBeEmpty();
    }

    // --- GetService ---

    [Fact]
    public void GetService_ChatClientMetadata_ReturnsMetadata()
    {
        var metadata = _client.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata;

        metadata.ShouldNotBeNull();
        metadata!.DefaultModelId.ShouldBe("test-model");
    }

    [Fact]
    public void GetService_UnknownType_ReturnsNull()
    {
        _client.GetService(typeof(string)).ShouldBeNull();
    }

    // --- GetResponseAsync: multiple user messages picks last ---

    [Fact]
    public async Task GetResponseAsync_MultipleUserMessages_UsesLastUserText()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "first message"),
            new(ChatRole.Assistant, "response"),
            new(ChatRole.User, "second message")
        };

        var response = await _client.GetResponseAsync(messages, cancellationToken: TestContext.Current.CancellationToken);

        response.Messages[0].Text.ShouldContain("second message");
        response.Messages[0].Text.ShouldNotContain("first message");
    }

    // --- Default model when null ---

    [Fact]
    public void Constructor_NullModelId_FallsBackToDefault()
    {
        using var client = new FallbackChatClient(null, NullLogger<FallbackChatClient>.Instance);
        var metadata = client.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata;

        metadata.ShouldNotBeNull();
        metadata!.DefaultModelId.ShouldBe("weave-local");
    }
}
