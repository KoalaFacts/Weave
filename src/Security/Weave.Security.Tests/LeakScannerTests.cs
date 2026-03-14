using Microsoft.Extensions.Logging;
using Weave.Security.Scanning;

namespace Weave.Security.Tests;

public sealed class LeakScannerTests
{
    private readonly LeakScanner _scanner = new(Substitute.For<ILogger<LeakScanner>>());

    private static ScanContext TestContext => new()
    {
        WorkspaceId = "test",
        SourceComponent = "test",
        Direction = ScanDirection.Outbound
    };

    [Fact]
    public async Task ScanStringAsync_WithCleanContent_ReturnsClean()
    {
        var result = await _scanner.ScanStringAsync("Hello, world!", TestContext);

        result.HasLeaks.ShouldBeFalse();
        result.Findings.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("AKIAIOSFODNN7EXAMPLE", "aws_access_key")]
    [InlineData("ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghij", "github_token")]
    [InlineData("sk-ant-abc123def456ghi789jkl012", "anthropic_key")]
    [InlineData("xoxb-1234567890-abcdefghij", "slack_token")]
    [InlineData("Bearer eyJhbGciOiJIUzI1NiJ9.test", "bearer_token")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----", "private_key_pem")]
    public async Task ScanStringAsync_WithKnownPatterns_DetectsLeak(string content, string expectedPattern)
    {
        var result = await _scanner.ScanStringAsync(content, TestContext);

        result.HasLeaks.ShouldBeTrue();
        result.Findings.ShouldContain(f => f.PatternName == expectedPattern);
    }

    [Fact]
    public async Task ScanStringAsync_WithJwt_DetectsLeak()
    {
        var jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abc123def456ghi789";
        var result = await _scanner.ScanStringAsync(jwt, TestContext);

        result.HasLeaks.ShouldBeTrue();
        result.Findings.ShouldContain(f => f.PatternName == "jwt_token");
    }

    [Fact]
    public async Task ScanAsync_WithBytePayload_ScansCorrectly()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("AKIAIOSFODNN7EXAMPLE");
        var result = await _scanner.ScanAsync(bytes, TestContext);

        result.HasLeaks.ShouldBeTrue();
    }

    [Fact]
    public void CalculateShannonEntropy_WithLowEntropy_ReturnsLowValue()
    {
        var entropy = LeakScanner.CalculateShannonEntropy("aaaaaaaaaa");
        entropy.ShouldBeLessThan(1.0);
    }

    [Fact]
    public void CalculateShannonEntropy_WithHighEntropy_ReturnsHighValue()
    {
        var entropy = LeakScanner.CalculateShannonEntropy("aB3$xZ9!mK2@pL5#nQ8&");
        entropy.ShouldBeGreaterThan(3.5);
    }
}
