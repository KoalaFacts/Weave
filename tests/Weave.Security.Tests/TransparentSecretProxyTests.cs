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
        var result = await _proxy.ScanResponseAsync("AKIAIOSFODNN7EXAMPLE", "test-ws");

        result.HasLeaks.ShouldBeTrue();
    }

    [Fact]
    public async Task ScanResponseAsync_WithCleanContent_ReturnsNoFindings()
    {
        var result = await _proxy.ScanResponseAsync("Hello, world!", "test-ws");

        result.HasLeaks.ShouldBeFalse();
        result.Findings.ShouldBeEmpty();
    }

    [Fact]
    public async Task ScanRequestAsync_WithLeaks_ReturnsFindings()
    {
        var result = await _proxy.ScanRequestAsync("ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghij", "test-ws");

        result.HasLeaks.ShouldBeTrue();
        result.Findings.ShouldContain(f => f.PatternName == "github_token");
    }

    [Fact]
    public async Task ScanRequestAsync_WithCleanContent_ReturnsNoFindings()
    {
        var result = await _proxy.ScanRequestAsync("just a normal request body", "test-ws");

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
}
