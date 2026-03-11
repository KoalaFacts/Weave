using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Weave.Security.Proxy;
using Weave.Security.Scanning;
using Weave.Shared.Secrets;
using Xunit;

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

        result.Should().Be("Authorization: my-secret-key");
    }

    [Fact]
    public void SubstitutePlaceholders_WithUnregisteredSecret_LeavesPlaceholder()
    {
        var result = _proxy.SubstitutePlaceholders("Authorization: {secret:unknown}");

        result.Should().Be("Authorization: {secret:unknown}");
    }

    [Fact]
    public void SubstitutePlaceholders_WithMultipleSecrets_ReplacesAll()
    {
        _proxy.RegisterSecret("key1", new SecretValue("value1"));
        _proxy.RegisterSecret("key2", new SecretValue("value2"));

        var result = _proxy.SubstitutePlaceholders("{secret:key1} and {secret:key2}");

        result.Should().Be("value1 and value2");
    }

    [Fact]
    public void UnregisterSecret_RemovesMapping()
    {
        _proxy.RegisterSecret("temp", new SecretValue("secret"));
        _proxy.UnregisterSecret("temp");

        var result = _proxy.SubstitutePlaceholders("{secret:temp}");
        result.Should().Be("{secret:temp}");
    }

    [Fact]
    public async Task ScanResponseAsync_WithLeaks_ReturnsFindings()
    {
        var result = await _proxy.ScanResponseAsync("AKIAIOSFODNN7EXAMPLE", "test-ws");

        result.HasLeaks.Should().BeTrue();
    }
}
