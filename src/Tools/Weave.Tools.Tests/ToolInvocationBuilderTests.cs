using Weave.Tools.Builders;
using Weave.Tools.Models;

namespace Weave.Tools.Tests;

public sealed class ToolInvocationBuilderTests
{
    [Fact]
    public void FromInput_ValidJson_ParsesMethodAndParams()
    {
        var result = ToolInvocationBuilder.FromInput("tool1", """{"method": "search", "query": "hello", "limit": "10"}""");

        result.Method.ShouldBe("search");
        result.Parameters.ShouldContainKey("query");
        result.Parameters["query"].ShouldBe("hello");
        result.Parameters.ShouldContainKey("limit");
        result.Parameters["limit"].ShouldBe("10");
        result.Parameters.ShouldNotContainKey("method");
    }

    [Fact]
    public void FromInput_JsonWithRawInput_ExtractsRawInput()
    {
        var result = ToolInvocationBuilder.FromInput("tool1", """{"method": "execute", "rawInput": "SELECT * FROM users"}""");

        result.Method.ShouldBe("execute");
        result.RawInput.ShouldBe("SELECT * FROM users");
    }

    [Fact]
    public void FromInput_JsonWithoutMethod_DefaultsToInvoke()
    {
        var result = ToolInvocationBuilder.FromInput("tool1", """{"key": "value"}""");

        result.Method.ShouldBe("invoke");
        result.Parameters.ShouldContainKey("key");
        result.Parameters["key"].ShouldBe("value");
    }

    [Fact]
    public void FromInput_NonStringJsonValue_UsesRawText()
    {
        var result = ToolInvocationBuilder.FromInput("tool1", """{"count": 42, "nested": {"a": 1}}""");

        result.Parameters["count"].ShouldBe("42");
        result.Parameters["nested"].ShouldContain("\"a\"");
    }

    [Fact]
    public void FromInput_PlainTextInput_FallsBackToRawInput()
    {
        var result = ToolInvocationBuilder.FromInput("tool1", "just plain text");

        result.Method.ShouldBe("invoke");
        result.RawInput.ShouldBe("just plain text");
        result.Parameters.ShouldBeEmpty();
    }

    [Fact]
    public void FromInput_MalformedJson_FallsBackToRawInput()
    {
        var result = ToolInvocationBuilder.FromInput("tool1", "{invalid json");

        result.Method.ShouldBe("invoke");
        result.RawInput.ShouldBe("{invalid json");
    }

    [Fact]
    public void FromInput_EmptyString_FallsBackToRawInput()
    {
        var result = ToolInvocationBuilder.FromInput("tool1", "");

        result.Method.ShouldBe("invoke");
        result.RawInput.ShouldBe("");
    }

    [Fact]
    public void FromInput_NullInput_FallsBackToRawInput()
    {
        var result = ToolInvocationBuilder.FromInput("tool1", null);

        result.Method.ShouldBe("invoke");
        result.RawInput.ShouldBeNull();
    }

    [Fact]
    public void FromInput_JsonArray_FallsBackToRawInput()
    {
        var result = ToolInvocationBuilder.FromInput("tool1", "[1, 2, 3]");

        result.Method.ShouldBe("invoke");
        result.RawInput.ShouldBe("[1, 2, 3]");
    }

    [Fact]
    public void FromInput_WhitespaceBeforeJson_StillParsed()
    {
        var result = ToolInvocationBuilder.FromInput("tool1", """   {"method": "fetch"}""");

        result.Method.ShouldBe("fetch");
    }

    [Fact]
    public void DescribeSchema_NoParameters_ReturnsDescriptionOnly()
    {
        var schema = new ToolSchema
        {
            ToolName = "test",
            Description = "A test tool",
            Parameters = []
        };

        var result = ToolInvocationBuilder.DescribeSchema(schema);

        result.ShouldBe("A test tool");
    }

    [Fact]
    public void DescribeSchema_SingleRequiredParameter_IncludesDetails()
    {
        var schema = new ToolSchema
        {
            ToolName = "search",
            Description = "Search tool",
            Parameters =
            [
                new ToolParameter
                {
                    Name = "query",
                    Type = "string",
                    Description = "Search query",
                    Required = true
                }
            ]
        };

        var result = ToolInvocationBuilder.DescribeSchema(schema);

        result.ShouldContain("query");
        result.ShouldContain("string");
        result.ShouldContain("required");
        result.ShouldContain("Search query");
    }

    [Fact]
    public void DescribeSchema_OptionalParameter_OmitsRequiredLabel()
    {
        var schema = new ToolSchema
        {
            ToolName = "search",
            Description = "Search tool",
            Parameters =
            [
                new ToolParameter
                {
                    Name = "limit",
                    Type = "int",
                    Description = "Max results",
                    Required = false
                }
            ]
        };

        var result = ToolInvocationBuilder.DescribeSchema(schema);

        result.ShouldContain("limit");
        result.ShouldNotContain("int, required");
    }

    [Fact]
    public void DescribeSchema_MultipleParameters_SemicolonSeparated()
    {
        var schema = new ToolSchema
        {
            ToolName = "search",
            Description = "Search tool",
            Parameters =
            [
                new ToolParameter
                {
                    Name = "query",
                    Type = "string",
                    Description = "Search query",
                    Required = true
                },
                new ToolParameter
                {
                    Name = "limit",
                    Type = "int",
                    Description = "Max results",
                    Required = false
                }
            ]
        };

        var result = ToolInvocationBuilder.DescribeSchema(schema);

        result.ShouldContain(";");
        result.ShouldContain("JSON object");
    }
}
