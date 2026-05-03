using Weave.Shared.Secrets;

namespace Weave.Shared.Tests;

public sealed class SecretSafeExceptionTests
{
    [Fact]
    public void Constructor_WithPasswordInMessage_RedactsValue()
    {
        var ex = new SecretSafeException("Failed with password=SuperSecret123");

        ex.Message.ShouldContain("***REDACTED***");
        ex.Message.ShouldNotContain("SuperSecret123");
    }

    [Fact]
    public void Constructor_WithTokenInMessage_RedactsValue()
    {
        var ex = new SecretSafeException("Auth token:ghp_abc123def456 expired");

        ex.Message.ShouldContain("***REDACTED***");
        ex.Message.ShouldNotContain("ghp_abc123def456");
    }

    [Fact]
    public void Constructor_WithSecretInMessage_RedactsValue()
    {
        var ex = new SecretSafeException("secret=my-api-secret-value in config");

        ex.Message.ShouldContain("***REDACTED***");
        ex.Message.ShouldNotContain("my-api-secret-value");
    }

    [Fact]
    public void Constructor_WithApiKeyInMessage_RedactsValue()
    {
        var ex = new SecretSafeException("apikey=AKIAIOSFODNN7EXAMPLE was invalid");

        ex.Message.ShouldContain("***REDACTED***");
        ex.Message.ShouldNotContain("AKIAIOSFODNN7EXAMPLE");
    }

    [Fact]
    public void Constructor_WithBearerInMessage_RedactsValue()
    {
        var ex = new SecretSafeException("bearer=eyJhbGciOiJIUzI1NiJ9 rejected");

        ex.Message.ShouldContain("***REDACTED***");
        ex.Message.ShouldNotContain("eyJhbGciOiJIUzI1NiJ9");
    }

    [Fact]
    public void Constructor_WithCleanMessage_LeavesUnchanged()
    {
        var ex = new SecretSafeException("Connection timed out after 30 seconds");

        ex.Message.ShouldBe("Connection timed out after 30 seconds");
    }

    [Fact]
    public void Constructor_WithInnerException_PreservesInner()
    {
        var inner = new InvalidOperationException("inner error");
        var ex = new SecretSafeException("outer error", inner);

        ex.InnerException.ShouldBeSameAs(inner);
    }

    [Fact]
    public void Constructor_WithMultipleSecrets_RedactsAll()
    {
        var ex = new SecretSafeException("password=abc123 and api_key=xyz789 found");

        ex.Message.ShouldNotContain("abc123");
        ex.Message.ShouldNotContain("xyz789");
    }

    [Fact]
    public void ToString_DoesNotIncludeStackTrace()
    {
        var ex = new SecretSafeException("Test error");

        var result = ex.ToString();

        result.ShouldContain("SecretSafeException");
        result.ShouldContain("Test error");
        result.ShouldNotContain("at ");
    }

    [Fact]
    public void Constructor_WithCredentialInMessage_RedactsValue()
    {
        var ex = new SecretSafeException("credential:user:pass123 invalid");

        ex.Message.ShouldContain("***REDACTED***");
        ex.Message.ShouldNotContain("user:pass123");
    }

    // --- Edge cases ---

    [Fact]
    public void Constructor_PatternAtEndOfMessage_NoRedaction()
    {
        // "password" at end with no separator — nothing to redact
        var ex = new SecretSafeException("Enter your password");

        ex.Message.ShouldBe("Enter your password");
    }

    [Fact]
    public void Constructor_SeparatorAtEndOfMessage_NoRedaction()
    {
        // "password=" at end with no value — nothing to redact
        var ex = new SecretSafeException("password=");

        ex.Message.ShouldBe("password=");
    }

    [Fact]
    public void Constructor_ValueAtEndOfMessage_RedactsToEnd()
    {
        // Value extends to end of string (no terminator)
        var ex = new SecretSafeException("password=SuperSecret");

        ex.Message.ShouldContain("***REDACTED***");
        ex.Message.ShouldNotContain("SuperSecret");
    }

    [Fact]
    public void Constructor_CaseInsensitive_RedactsAllVariants()
    {
        var ex1 = new SecretSafeException("PASSWORD=abc123 done");
        var ex2 = new SecretSafeException("Password=abc123 done");
        var ex3 = new SecretSafeException("pAsSwOrD=abc123 done");

        ex1.Message.ShouldNotContain("abc123");
        ex2.Message.ShouldNotContain("abc123");
        ex3.Message.ShouldNotContain("abc123");
    }

    [Fact]
    public void Constructor_SpaceSeparator_RedactsValue()
    {
        var ex = new SecretSafeException("token abc123def456 found");

        ex.Message.ShouldContain("***REDACTED***");
        ex.Message.ShouldNotContain("abc123def456");
    }

    [Fact]
    public void Constructor_ColonSeparator_RedactsValue()
    {
        var ex = new SecretSafeException("key:my-secret-value;next");

        ex.Message.ShouldContain("***REDACTED***");
        ex.Message.ShouldNotContain("my-secret-value");
        ex.Message.ShouldContain("next");
    }

    [Fact]
    public void Constructor_ApiHyphenKey_RedactsValue()
    {
        var ex = new SecretSafeException("api-key=xyz789 was rejected");

        ex.Message.ShouldContain("***REDACTED***");
        ex.Message.ShouldNotContain("xyz789");
    }

    [Fact]
    public void Constructor_EmptyMessage_ReturnsEmpty()
    {
        var ex = new SecretSafeException("");

        ex.Message.ShouldBe("");
    }

    [Fact]
    public void Constructor_ValueTerminatedBySemicolon_RedactsCorrectly()
    {
        var ex = new SecretSafeException("config: secret=myvalue;next=other");

        ex.Message.ShouldNotContain("myvalue");
        ex.Message.ShouldContain("next=other");
    }

    [Fact]
    public void Constructor_ValueTerminatedByNewline_RedactsCorrectly()
    {
        var ex = new SecretSafeException("secret=hidden\nnext line");

        ex.Message.ShouldNotContain("hidden");
        ex.Message.ShouldContain("next line");
    }
}
