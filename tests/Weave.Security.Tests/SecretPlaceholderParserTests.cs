using Weave.Security.Scanning;

namespace Weave.Security.Tests;

public sealed class SecretPlaceholderParserTests
{
    // --- EnumeratePaths ---

    [Fact]
    public void EnumeratePaths_SingleSecret_ExtractsPath()
    {
        var paths = SecretPlaceholderParser.EnumeratePaths("token={secret:api-key}").ToList();

        paths.ShouldBe(["api-key"]);
    }

    [Fact]
    public void EnumeratePaths_MultipleSecrets_ExtractsAll()
    {
        var paths = SecretPlaceholderParser.EnumeratePaths("user={secret:db-user}&pass={secret:db-pass}").ToList();

        paths.ShouldBe(["db-user", "db-pass"]);
    }

    [Fact]
    public void EnumeratePaths_NoSecrets_ReturnsEmpty()
    {
        var paths = SecretPlaceholderParser.EnumeratePaths("plain text").ToList();

        paths.ShouldBeEmpty();
    }

    [Fact]
    public void EnumeratePaths_EmptyString_ReturnsEmpty()
    {
        var paths = SecretPlaceholderParser.EnumeratePaths("").ToList();

        paths.ShouldBeEmpty();
    }

    [Fact]
    public void EnumeratePaths_MissingCloseBrace_StopsEnumeration()
    {
        var paths = SecretPlaceholderParser.EnumeratePaths("{secret:unclosed").ToList();

        paths.ShouldBeEmpty();
    }

    [Fact]
    public void EnumeratePaths_PathWithSlashes_PreservesFullPath()
    {
        var paths = SecretPlaceholderParser.EnumeratePaths("{secret:vault/db/password}").ToList();

        paths.ShouldBe(["vault/db/password"]);
    }

    [Fact]
    public void EnumeratePaths_AdjacentSecrets_ExtractsBoth()
    {
        var paths = SecretPlaceholderParser.EnumeratePaths("{secret:a}{secret:b}").ToList();

        paths.ShouldBe(["a", "b"]);
    }

    // --- Substitute ---

    [Fact]
    public void Substitute_RegisteredPath_ReplacesValue()
    {
        var result = SecretPlaceholderParser.Substitute(
            "token={secret:api-key}",
            path => path == "api-key" ? "my-secret-value" : null);

        result.ShouldBe("token=my-secret-value");
    }

    [Fact]
    public void Substitute_UnregisteredPath_LeavesPlaceholder()
    {
        var result = SecretPlaceholderParser.Substitute(
            "token={secret:unknown}",
            _ => null);

        result.ShouldBe("token={secret:unknown}");
    }

    [Fact]
    public void Substitute_MultiplePaths_ReplacesAll()
    {
        var secrets = new Dictionary<string, string>
        {
            ["db-user"] = "admin",
            ["db-pass"] = "s3cret"
        };

        var result = SecretPlaceholderParser.Substitute(
            "user={secret:db-user}&pass={secret:db-pass}",
            path => secrets.GetValueOrDefault(path));

        result.ShouldBe("user=admin&pass=s3cret");
    }

    [Fact]
    public void Substitute_MixedKnownAndUnknown_ReplacesOnlyKnown()
    {
        var result = SecretPlaceholderParser.Substitute(
            "a={secret:known}&b={secret:missing}",
            path => path == "known" ? "val" : null);

        result.ShouldBe("a=val&b={secret:missing}");
    }

    [Fact]
    public void Substitute_SamePlaceholderTwice_ReplacesBoth()
    {
        var result = SecretPlaceholderParser.Substitute(
            "first:{secret:key}, second:{secret:key}",
            _ => "val");

        result.ShouldBe("first:val, second:val");
    }

    [Fact]
    public void Substitute_EmptyContent_ReturnsEmpty()
    {
        var result = SecretPlaceholderParser.Substitute("", _ => "val");

        result.ShouldBe("");
    }

    [Fact]
    public void Substitute_NoPlaceholders_ReturnsUnchanged()
    {
        var result = SecretPlaceholderParser.Substitute("plain text", _ => "val");

        result.ShouldBe("plain text");
    }

    [Fact]
    public void Substitute_MalformedPlaceholder_LeavesUnchanged()
    {
        var result = SecretPlaceholderParser.Substitute("{secret:key", _ => "val");

        result.ShouldBe("{secret:key");
    }

    [Fact]
    public void Substitute_AdjacentPlaceholders_ReplacesBoth()
    {
        var result = SecretPlaceholderParser.Substitute(
            "{secret:a}{secret:b}",
            path => path == "a" ? "1" : "2");

        result.ShouldBe("12");
    }

    [Fact]
    public void Substitute_PathWithSlashes_Works()
    {
        var result = SecretPlaceholderParser.Substitute(
            "pass={secret:vault/db/password}",
            path => path == "vault/db/password" ? "hunter2" : null);

        result.ShouldBe("pass=hunter2");
    }
}
