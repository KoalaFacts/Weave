

namespace Weave.Cli.Tests;

public class TuiAppTests
{
    // ── ParseCommand ───────────────────────────────────────────────

    [Fact]
    public void ParseCommand_SimpleCommand_ReturnsNameNoArgs()
    {
        var (name, args) = TuiCommandParser.Parse("open");
        name.ShouldBe("open");
        args.ShouldBeNull();
    }

    [Fact]
    public void ParseCommand_CommandWithArgs_SplitsCorrectly()
    {
        var (name, args) = TuiCommandParser.Parse("open my-workspace");
        name.ShouldBe("open");
        args.ShouldBe("my-workspace");
    }

    [Fact]
    public void ParseCommand_LeadingSlash_StrippedByCallerNotParser()
    {
        // ParseCommand receives the already-stripped text (no slash).
        // If called with a slash, it treats the whole thing as the name.
        var (name, args) = TuiCommandParser.Parse("/open");
        name.ShouldBe("/open");
        args.ShouldBeNull();
    }

    [Fact]
    public void ParseCommand_EmptyInput_ReturnsEmptyName()
    {
        var (name, args) = TuiCommandParser.Parse("");
        name.ShouldBe(string.Empty);
        args.ShouldBeNull();
    }

    [Fact]
    public void ParseCommand_WhitespaceOnly_ReturnsEmptyName()
    {
        var (name, args) = TuiCommandParser.Parse("   ");
        name.ShouldBe(string.Empty);
        args.ShouldBeNull();
    }

    [Fact]
    public void ParseCommand_ArgsWithSpaces_PreservesFullArgString()
    {
        var (name, args) = TuiCommandParser.Parse("use my cool agent");
        name.ShouldBe("use");
        args.ShouldBe("my cool agent");
    }

    [Fact]
    public void ParseCommand_UpperCase_NormalizesToLower()
    {
        var (name, _) = TuiCommandParser.Parse("OPEN");
        name.ShouldBe("open");
    }

    [Fact]
    public void ParseCommand_TrailingSpaces_TrimmedArgs()
    {
        var (name, args) = TuiCommandParser.Parse("open   workspace   ");
        name.ShouldBe("open");
        args.ShouldBe("workspace");
    }

    // ── LevenshteinDistance ─────────────────────────────────────────

    [Fact]
    public void LevenshteinDistance_IdenticalStrings_ReturnsZero()
    {
        TuiCommandParser.LevenshteinDistance("open", "open").ShouldBe(0);
    }

    [Fact]
    public void LevenshteinDistance_EmptyAndNonEmpty_ReturnsLength()
    {
        TuiCommandParser.LevenshteinDistance("", "abc").ShouldBe(3);
        TuiCommandParser.LevenshteinDistance("abc", "").ShouldBe(3);
    }

    [Fact]
    public void LevenshteinDistance_BothEmpty_ReturnsZero()
    {
        TuiCommandParser.LevenshteinDistance("", "").ShouldBe(0);
    }

    [Fact]
    public void LevenshteinDistance_SingleSubstitution_ReturnsOne()
    {
        TuiCommandParser.LevenshteinDistance("cat", "bat").ShouldBe(1);
    }

    [Fact]
    public void LevenshteinDistance_SingleInsertion_ReturnsOne()
    {
        TuiCommandParser.LevenshteinDistance("open", "ope").ShouldBe(1);
    }

    [Fact]
    public void LevenshteinDistance_CaseInsensitive_MatchesMixed()
    {
        // The implementation lowercases before comparing.
        TuiCommandParser.LevenshteinDistance("Open", "open").ShouldBe(0);
    }

    [Theory]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("abc", "xyz", 3)]
    [InlineData("flaw", "lawn", 2)]
    public void LevenshteinDistance_KnownPairs_MatchExpected(string a, string b, int expected)
    {
        TuiCommandParser.LevenshteinDistance(a, b).ShouldBe(expected);
    }

    // ── SuggestCommand ─────────────────────────────────────────────

    [Fact]
    public void SuggestCommand_ExactPrefix_ReturnsMatch()
    {
        TuiCommandParser.Suggest("ope").ShouldBe("open");
    }

    [Fact]
    public void SuggestCommand_Typo_ReturnsClosest()
    {
        // "hlep" → Levenshtein 2 from "help"
        TuiCommandParser.Suggest("hlep").ShouldBe("help");
    }

    [Fact]
    public void SuggestCommand_Empty_ReturnsNull()
    {
        TuiCommandParser.Suggest("").ShouldBeNull();
    }

    [Fact]
    public void SuggestCommand_Whitespace_ReturnsNull()
    {
        TuiCommandParser.Suggest("   ").ShouldBeNull();
    }

    [Fact]
    public void SuggestCommand_TooFar_ReturnsNull()
    {
        // "zzzzz" has no match within Levenshtein distance 2.
        TuiCommandParser.Suggest("zzzzz").ShouldBeNull();
    }

    [Fact]
    public void SuggestCommand_ExactMatch_ReturnsCommand()
    {
        TuiCommandParser.Suggest("help").ShouldBe("help");
    }

    [Theory]
    [InlineData("wat", "watch")]
    [InlineData("ver", "version")]
    [InlineData("up", "up")]
    [InlineData("ag", "agent")]
    public void SuggestCommand_Prefixes_ReturnExpected(string typed, string expected)
    {
        TuiCommandParser.Suggest(typed).ShouldBe(expected);
    }

    // ── ColorStatus ────────────────────────────────────────────────

    [Theory]
    [InlineData("running")]
    [InlineData("active")]
    [InlineData("connected")]
    [InlineData("ready")]
    [InlineData("healthy")]
    public void ColorStatus_SuccessStatuses_ContainStatusText(string status)
    {
        var result = StatusMarkup.ColorStatus(status);
        result.ShouldContain(status);
        result.ShouldStartWith("[rgb(");
        result.ShouldEndWith("[/]");
    }

    [Theory]
    [InlineData("stopped")]
    [InlineData("idle")]
    [InlineData("disconnected")]
    public void ColorStatus_InactiveStatuses_ContainStatusText(string status)
    {
        var result = StatusMarkup.ColorStatus(status);
        result.ShouldContain(status);
    }

    [Fact]
    public void ColorStatus_ErrorStatus_ContainStatusText()
    {
        var result = StatusMarkup.ColorStatus("error");
        result.ShouldContain("error");
    }

    [Fact]
    public void ColorStatus_CaseInsensitive_Works()
    {
        var result = StatusMarkup.ColorStatus("RUNNING");
        result.ShouldContain("RUNNING");
        result.ShouldStartWith("[rgb(");
    }

    // ── ColorTag ───────────────────────────────────────────────────

    [Fact]
    public void ColorTag_ProducesValidMarkup()
    {
        var result = StatusMarkup.ColorTag(new Spectre.Console.Color(255, 0, 128), "test");
        result.ShouldBe("[rgb(255,0,128)]test[/]");
    }

    [Fact]
    public void ColorTag_EscapesMarkupCharacters()
    {
        var result = StatusMarkup.ColorTag(new Spectre.Console.Color(0, 0, 0), "[bold]text[/]");
        result.ShouldContain("[[bold]]");
    }
}
