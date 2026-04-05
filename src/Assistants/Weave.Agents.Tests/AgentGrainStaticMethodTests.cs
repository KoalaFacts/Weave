using Microsoft.Extensions.AI;
using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Tools.Models;

namespace Weave.Agents.Tests;

public sealed class AgentGrainStaticMethodTests
{
    // --- CreateToolInvocation: JSON parsing ---

    [Fact]
    public void CreateToolInvocation_ValidJson_ParsesMethodAndParams()
    {
        var result = AgentGrain.CreateToolInvocation("my-tool",
            """{"method": "search", "query": "hello", "limit": "10"}""");

        result.ToolName.ShouldBe("my-tool");
        result.Method.ShouldBe("search");
        result.Parameters["query"].ShouldBe("hello");
        result.Parameters["limit"].ShouldBe("10");
        result.Parameters.ShouldNotContainKey("method");
    }

    [Fact]
    public void CreateToolInvocation_JsonWithRawInput_ExtractsRawInput()
    {
        var result = AgentGrain.CreateToolInvocation("my-tool",
            """{"method": "execute", "rawInput": "SELECT * FROM users"}""");

        result.Method.ShouldBe("execute");
        result.RawInput.ShouldBe("SELECT * FROM users");
        result.Parameters.ShouldNotContainKey("rawInput");
    }

    [Fact]
    public void CreateToolInvocation_JsonWithoutMethod_DefaultsToInvoke()
    {
        var result = AgentGrain.CreateToolInvocation("my-tool",
            """{"key": "value"}""");

        result.Method.ShouldBe("invoke");
        result.Parameters["key"].ShouldBe("value");
    }

    [Fact]
    public void CreateToolInvocation_NonStringJsonValue_UsesRawText()
    {
        var result = AgentGrain.CreateToolInvocation("my-tool",
            """{"count": 42, "nested": {"a": 1}}""");

        result.Parameters["count"].ShouldBe("42");
        result.Parameters["nested"].ShouldContain("\"a\"");
    }

    [Fact]
    public void CreateToolInvocation_PlainTextInput_FallsBackToRawInput()
    {
        var result = AgentGrain.CreateToolInvocation("my-tool", "just plain text");

        result.ToolName.ShouldBe("my-tool");
        result.Method.ShouldBe("invoke");
        result.RawInput.ShouldBe("just plain text");
        result.Parameters.ShouldBeEmpty();
    }

    [Fact]
    public void CreateToolInvocation_MalformedJson_FallsBackToRawInput()
    {
        var result = AgentGrain.CreateToolInvocation("my-tool", "{invalid json");

        result.Method.ShouldBe("invoke");
        result.RawInput.ShouldBe("{invalid json");
    }

    [Fact]
    public void CreateToolInvocation_EmptyString_FallsBackToRawInput()
    {
        var result = AgentGrain.CreateToolInvocation("my-tool", "");

        result.Method.ShouldBe("invoke");
        result.RawInput.ShouldBe("");
    }

    [Fact]
    public void CreateToolInvocation_NullInput_FallsBackToRawInput()
    {
        var result = AgentGrain.CreateToolInvocation("my-tool", null!);

        result.Method.ShouldBe("invoke");
    }

    [Fact]
    public void CreateToolInvocation_JsonArray_FallsBackToRawInput()
    {
        var result = AgentGrain.CreateToolInvocation("my-tool", "[1, 2, 3]");

        // Arrays are not objects, should fall back
        result.Method.ShouldBe("invoke");
        result.RawInput.ShouldBe("[1, 2, 3]");
    }

    [Fact]
    public void CreateToolInvocation_WhitespaceBeforeJson_StillParsed()
    {
        var result = AgentGrain.CreateToolInvocation("my-tool",
            """   {"method": "fetch"}""");

        result.Method.ShouldBe("fetch");
    }

    // --- BuildToolDescription ---

    [Fact]
    public void BuildToolDescription_NoParameters_ReturnsDescriptionOnly()
    {
        var schema = new ToolSchema
        {
            ToolName = "test",
            Description = "A test tool"
        };

        AgentGrain.BuildToolDescription(schema).ShouldBe("A test tool");
    }

    [Fact]
    public void BuildToolDescription_SingleParameter_IncludesParamDetails()
    {
        var schema = new ToolSchema
        {
            ToolName = "test",
            Description = "A test tool",
            Parameters =
            [
                new ToolParameter { Name = "query", Type = "string", Description = "Search query", Required = true }
            ]
        };

        var description = AgentGrain.BuildToolDescription(schema);
        description.ShouldContain("query");
        description.ShouldContain("string");
        description.ShouldContain("required");
        description.ShouldContain("Search query");
    }

    [Fact]
    public void BuildToolDescription_OptionalParameter_OmitsRequiredLabel()
    {
        var schema = new ToolSchema
        {
            ToolName = "test",
            Description = "A tool",
            Parameters =
            [
                new ToolParameter { Name = "limit", Type = "int", Description = "Max results", Required = false }
            ]
        };

        var description = AgentGrain.BuildToolDescription(schema);
        description.ShouldContain("limit");
        // The parameter itself should NOT be labeled "required"
        // (the suffix "JSON object if multiple fields are required" is unrelated)
        description.ShouldNotContain("int, required");
    }

    [Fact]
    public void BuildToolDescription_MultipleParameters_SemicolonSeparated()
    {
        var schema = new ToolSchema
        {
            ToolName = "test",
            Description = "A tool",
            Parameters =
            [
                new ToolParameter { Name = "a", Type = "string", Description = "First" },
                new ToolParameter { Name = "b", Type = "int", Description = "Second" }
            ]
        };

        var description = AgentGrain.BuildToolDescription(schema);
        description.ShouldContain(";");
        description.ShouldContain("JSON object");
    }

    // --- ToChatMessage ---

    [Fact]
    public void ToChatMessage_UserRole_MapsCorrectly()
    {
        var msg = new ConversationMessage { Role = "user", Content = "Hello" };

        var chatMsg = AgentGrain.ToChatMessage(msg);

        chatMsg.Role.ShouldBe(ChatRole.User);
        chatMsg.Text.ShouldBe("Hello");
    }

    [Fact]
    public void ToChatMessage_AssistantRole_MapsCorrectly()
    {
        var msg = new ConversationMessage { Role = "assistant", Content = "Hi there" };

        var chatMsg = AgentGrain.ToChatMessage(msg);

        chatMsg.Role.ShouldBe(ChatRole.Assistant);
    }

    [Fact]
    public void ToChatMessage_SystemRole_MapsCorrectly()
    {
        var msg = new ConversationMessage { Role = "system", Content = "You are a bot" };

        var chatMsg = AgentGrain.ToChatMessage(msg);

        chatMsg.Role.ShouldBe(ChatRole.System);
    }

    [Fact]
    public void ToChatMessage_ToolRole_MapsCorrectly()
    {
        var msg = new ConversationMessage { Role = "tool", Content = "result" };

        var chatMsg = AgentGrain.ToChatMessage(msg);

        chatMsg.Role.ShouldBe(ChatRole.Tool);
    }

    [Fact]
    public void ToChatMessage_UnknownRole_DefaultsToUser()
    {
        var msg = new ConversationMessage { Role = "something-else", Content = "test" };

        var chatMsg = AgentGrain.ToChatMessage(msg);

        chatMsg.Role.ShouldBe(ChatRole.User);
    }

    [Fact]
    public void ToChatMessage_CaseInsensitiveRole_MapsCorrectly()
    {
        var msg = new ConversationMessage { Role = "ASSISTANT", Content = "test" };

        var chatMsg = AgentGrain.ToChatMessage(msg);

        chatMsg.Role.ShouldBe(ChatRole.Assistant);
    }

    [Fact]
    public void ToChatMessage_PreservesTimestamp()
    {
        var timestamp = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var msg = new ConversationMessage { Role = "user", Content = "test", Timestamp = timestamp };

        var chatMsg = AgentGrain.ToChatMessage(msg);

        chatMsg.CreatedAt.ShouldBe(timestamp);
    }
}
