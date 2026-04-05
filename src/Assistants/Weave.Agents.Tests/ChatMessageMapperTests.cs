using Microsoft.Extensions.AI;
using Weave.Agents.Models;
using Weave.Agents.Pipeline;

namespace Weave.Agents.Tests;

public sealed class ChatMessageMapperTests
{
    // --- ToChatMessage ---

    [Fact]
    public void ToChatMessage_UserRole_MapsCorrectly()
    {
        var msg = new ConversationMessage { Role = "user", Content = "Hello" };

        var chatMsg = ChatMessageMapper.ToChatMessage(msg);

        chatMsg.Role.ShouldBe(ChatRole.User);
        chatMsg.Text.ShouldBe("Hello");
    }

    [Fact]
    public void ToChatMessage_AssistantRole_MapsCorrectly()
    {
        var msg = new ConversationMessage { Role = "assistant", Content = "Hi there" };

        var chatMsg = ChatMessageMapper.ToChatMessage(msg);

        chatMsg.Role.ShouldBe(ChatRole.Assistant);
    }

    [Fact]
    public void ToChatMessage_SystemRole_MapsCorrectly()
    {
        var msg = new ConversationMessage { Role = "system", Content = "You are a bot" };

        var chatMsg = ChatMessageMapper.ToChatMessage(msg);

        chatMsg.Role.ShouldBe(ChatRole.System);
    }

    [Fact]
    public void ToChatMessage_ToolRole_MapsCorrectly()
    {
        var msg = new ConversationMessage { Role = "tool", Content = "result" };

        var chatMsg = ChatMessageMapper.ToChatMessage(msg);

        chatMsg.Role.ShouldBe(ChatRole.Tool);
    }

    [Fact]
    public void ToChatMessage_UnknownRole_DefaultsToUser()
    {
        var msg = new ConversationMessage { Role = "something-else", Content = "test" };

        var chatMsg = ChatMessageMapper.ToChatMessage(msg);

        chatMsg.Role.ShouldBe(ChatRole.User);
    }

    [Fact]
    public void ToChatMessage_CaseInsensitiveRole_MapsCorrectly()
    {
        var msg = new ConversationMessage { Role = "ASSISTANT", Content = "test" };

        var chatMsg = ChatMessageMapper.ToChatMessage(msg);

        chatMsg.Role.ShouldBe(ChatRole.Assistant);
    }

    [Fact]
    public void ToChatMessage_PreservesTimestamp()
    {
        var timestamp = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var msg = new ConversationMessage { Role = "user", Content = "test", Timestamp = timestamp };

        var chatMsg = ChatMessageMapper.ToChatMessage(msg);

        chatMsg.CreatedAt.ShouldBe(timestamp);
    }

    // --- ToConversationMessages ---

    [Fact]
    public void ToConversationMessages_PlainText_YieldsSingleMessage()
    {
        var chatMsg = new ChatMessage(ChatRole.Assistant, "Hello world");

        var results = ChatMessageMapper.ToConversationMessages(chatMsg).ToList();

        results.Count.ShouldBe(1);
        results[0].Role.ShouldBe("assistant");
        results[0].Content.ShouldBe("Hello world");
    }

    [Fact]
    public void ToConversationMessages_FunctionCall_YieldsToolMessage()
    {
        var args = new Dictionary<string, object?> { ["query"] = "test" };
        var functionCall = new FunctionCallContent("call-1", "my-tool", args);
        var chatMsg = new ChatMessage(ChatRole.Assistant, [functionCall]);

        var results = ChatMessageMapper.ToConversationMessages(chatMsg).ToList();

        results.Count.ShouldBe(1);
        results[0].Role.ShouldBe("tool");
        results[0].Content.ShouldContain("my-tool");
    }

    [Fact]
    public void ToConversationMessages_FunctionResult_YieldsToolMessage()
    {
        var functionResult = new FunctionResultContent("call-1", "output data");
        var chatMsg = new ChatMessage(ChatRole.Tool, [functionResult]);

        var results = ChatMessageMapper.ToConversationMessages(chatMsg).ToList();

        results.Count.ShouldBe(1);
        results[0].Role.ShouldBe("tool");
        results[0].Content.ShouldContain("output data");
    }

    [Fact]
    public void ToConversationMessages_EmptyText_SkipsTextMessage()
    {
        var chatMsg = new ChatMessage(ChatRole.Assistant, "   ");

        var results = ChatMessageMapper.ToConversationMessages(chatMsg).ToList();

        results.ShouldBeEmpty();
    }

    [Fact]
    public void ToConversationMessages_TextAndFunctionCall_YieldsBoth()
    {
        var chatMsg = new ChatMessage(ChatRole.Assistant, "thinking...");
        chatMsg.Contents.Add(new FunctionCallContent("call-1", "my-tool"));

        var results = ChatMessageMapper.ToConversationMessages(chatMsg).ToList();

        results.Count.ShouldBe(2);
    }

    [Fact]
    public void ToConversationMessages_PreservesTimestamp()
    {
        var timestamp = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var chatMsg = new ChatMessage(ChatRole.Assistant, "Hello")
        {
            CreatedAt = timestamp
        };

        var results = ChatMessageMapper.ToConversationMessages(chatMsg).ToList();

        results[0].Timestamp.ShouldBe(timestamp);
    }
}
