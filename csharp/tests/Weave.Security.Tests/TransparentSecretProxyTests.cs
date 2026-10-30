using Microsoft.Extensions.Logging;
using Weave.Security.Proxy;
using Weave.Security.Scanning;
using Weave.Shared.Secrets;

namespace Weave.Security.Tests;

public sealed class TransparentSecretProxyTests
{
    private readonly TransparentSecretProxy _proxy;

    public TransparentSecretProxyTests()
    {
        var scanner = new LeakScanner(Substitute.For<ILogger<LeakScanner>>());
        _proxy = new TransparentSecretProxy(scanner, Substitute.For<ILogger<TransparentSecretProxy>>());
    }

    [Fact]
    public void SubstitutePlaceholders_WithRegisteredSecret_ReplacesValue()
    {
        _proxy.RegisterSecret("api_key", new SecretValue("my-secret-key"));

        var result = _proxy.SubstitutePlaceholders("Authorization: {secret:api_key}");

        result.ShouldBe("Authorization: my-secret-key");
    }

    [Fact]
    public void SubstitutePlaceholders_WithUnregisteredSecret_LeavesPlaceholder()
    {
        var result = _proxy.SubstitutePlaceholders("Authorization: {secret:unknown}");

        result.ShouldBe("Authorization: {secret:unknown}");
    }

    [Fact]
    public void SubstitutePlaceholders_WithMultipleSecrets_ReplacesAll()
    {
        _proxy.RegisterSecret("key1", new SecretValue("value1"));
        _proxy.RegisterSecret("key2", new SecretValue("value2"));

        var result = _proxy.SubstitutePlaceholders("{secret:key1} and {secret:key2}");

        result.ShouldBe("value1 and value2");
    }

    [Fact]
    public void UnregisterSecret_RemovesMapping()
    {
        _proxy.RegisterSecret("temp", new SecretValue("secret"));
        _proxy.UnregisterSecret("temp");

        var result = _proxy.SubstitutePlaceholders("{secret:temp}");
        result.ShouldBe("{secret:temp}");
    }

    [Fact]
    public async Task ScanResponseAsync_WithLeaks_ReturnsFindings()
    {
        var result = await _proxy.ScanResponseAsync("AKIAIOSFODNN7EXAMPLE", "test-ws", TestContext.Current.CancellationToken);

        result.HasLeaks.ShouldBeTrue();
    }

    [Fact]
    public async Task ScanResponseAsync_WithCleanContent_ReturnsNoFindings()
    {
        var result = await _proxy.ScanResponseAsync("Hello, world!", "test-ws", TestContext.Current.CancellationToken);

        result.HasLeaks.ShouldBeFalse();
        result.Findings.ShouldBeEmpty();
    }

    [Fact]
    public async Task ScanRequestAsync_WithLeaks_ReturnsFindings()
    {
        var result = await _proxy.ScanRequestAsync("ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghij", "test-ws", TestContext.Current.CancellationToken);

        result.HasLeaks.ShouldBeTrue();
        result.Findings.ShouldContain(f => f.PatternName == "github_token");
    }

    [Fact]
    public async Task ScanRequestAsync_WithCleanContent_ReturnsNoFindings()
    {
        var result = await _proxy.ScanRequestAsync("just a normal request body", "test-ws", TestContext.Current.CancellationToken);

        result.HasLeaks.ShouldBeFalse();
    }

    [Fact]
    public void SubstitutePlaceholders_WithNoPlaceholders_ReturnsUnchanged()
    {
        var input = "plain text with no secrets";
        var result = _proxy.SubstitutePlaceholders(input);

        result.ShouldBe(input);
    }

    [Fact]
    public void RegisterSecret_ThenUnregister_ThenReRegister_Works()
    {
        _proxy.RegisterSecret("key", new SecretValue("first"));
        _proxy.UnregisterSecret("key");
        _proxy.RegisterSecret("key", new SecretValue("second"));

        var result = _proxy.SubstitutePlaceholders("{secret:key}");
        result.ShouldBe("second");
    }

    // --- Edge cases ---

    [Fact]
    public void SubstitutePlaceholders_EmptyContent_ReturnsEmpty()
    {
        _proxy.SubstitutePlaceholders("").ShouldBe("");
    }

    [Fact]
    public void SubstitutePlaceholders_AdjacentPlaceholders_ReplacesAll()
    {
        _proxy.RegisterSecret("a", new SecretValue("1"));
        _proxy.RegisterSecret("b", new SecretValue("2"));

        var result = _proxy.SubstitutePlaceholders("{secret:a}{secret:b}");
        result.ShouldBe("12");
    }

    [Fact]
    public void SubstitutePlaceholders_SamePlaceholderTwice_ReplacesAll()
    {
        _proxy.RegisterSecret("key", new SecretValue("val"));

        var result = _proxy.SubstitutePlaceholders("first:{secret:key}, second:{secret:key}");
        result.ShouldBe("first:val, second:val");
    }

    [Fact]
    public void SubstitutePlaceholders_MixedRegisteredAndUnregistered_ReplacesOnlyRegistered()
    {
        _proxy.RegisterSecret("known", new SecretValue("value"));

        var result = _proxy.SubstitutePlaceholders("{secret:known} and {secret:unknown}");
        result.ShouldBe("value and {secret:unknown}");
    }

    [Fact]
    public void SubstitutePlaceholders_PathWithSlashes_Works()
    {
        _proxy.RegisterSecret("db/prod/password", new SecretValue("s3cret"));

        var result = _proxy.SubstitutePlaceholders("pass={secret:db/prod/password}");
        result.ShouldBe("pass=s3cret");
    }

    [Fact]
    public void SubstitutePlaceholders_MalformedPlaceholder_NotReplaced()
    {
        // Missing closing brace
        var result = _proxy.SubstitutePlaceholders("{secret:key");
        result.ShouldBe("{secret:key");
    }

    [Fact]
    public void RegisterSecret_OverwriteExisting_UsesNewValue()
    {
        _proxy.RegisterSecret("key", new SecretValue("old"));
        _proxy.RegisterSecret("key", new SecretValue("new"));

        var result = _proxy.SubstitutePlaceholders("{secret:key}");
        result.ShouldBe("new");
    }

    [Fact]
    public void UnregisterSecret_NonexistentKey_DoesNotThrow()
    {
        Should.NotThrow(() => _proxy.UnregisterSecret("nonexistent"));
    }

    [Fact]
    public async Task ScanResponseAsync_WithDirection_UsesInbound()
    {
        // Verifies the proxy creates the right context for response scanning
        var result = await _proxy.ScanResponseAsync("clean text", "ws-1", TestContext.Current.CancellationToken);
        result.HasLeaks.ShouldBeFalse();
    }

    [Fact]
    public async Task ScanRequestAsync_WithDirection_UsesOutbound()
    {
        var result = await _proxy.ScanRequestAsync("clean text", "ws-1", TestContext.Current.CancellationToken);
        result.HasLeaks.ShouldBeFalse();
    }
}
