using Weave.Shared.Strings;

namespace Weave.Shared.Tests;

public class ControlCharFilterTests
{
    [Theory]
    [InlineData("plain text 123")]
    [InlineData("a-b_c.d")]
    [InlineData("")]
    public void ReplaceControlChars_LeavesPrintableUnchanged(string input) =>
        ControlCharFilter.ReplaceControlChars(input).ShouldBe(input);

    [Fact]
    public void ReplaceControlChars_ReplacesAnsiEscape() =>
        ControlCharFilter.ReplaceControlChars("\u001b[31mred\u001b[0m").ShouldBe("?[31mred?[0m");

    [Fact]
    public void ReplaceControlChars_ReplacesNewlineTabAndBel() =>
        ControlCharFilter.ReplaceControlChars("a\nb\tc\u0007d").ShouldBe("a?b?c?d");

    [Fact]
    public void ReplaceControlChars_ReplacesDel() =>
        ControlCharFilter.ReplaceControlChars("before\u007fafter").ShouldBe("before?after");

    [Fact]
    public void ReplaceControlChars_PreservesNonControlAfterReplacement() =>
        ControlCharFilter.ReplaceControlChars("\u0001hello\u0002world").ShouldBe("?hello?world");
}
