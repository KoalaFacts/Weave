using Microsoft.Extensions.Logging;
using Weave.Security.Scanning;

namespace Weave.Security.Tests;

public sealed class LeakScannerTests
{
    private readonly LeakScanner _scanner = new(Substitute.For<ILogger<LeakScanner>>());

    private static ScanContext DefaultScanContext => new()
    {
        WorkspaceId = "test",
        SourceComponent = "test",
        Direction = ScanDirection.Outbound
    };

    [Fact]
    public async Task ScanStringAsync_WithCleanContent_ReturnsClean()
    {
        var result = await _scanner.ScanStringAsync("Hello, world!", DefaultScanContext, TestContext.Current.CancellationToken);

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
        var result = await _scanner.ScanStringAsync(content, DefaultScanContext, TestContext.Current.CancellationToken);

        result.HasLeaks.ShouldBeTrue();
        result.Findings.ShouldContain(f => f.PatternName == expectedPattern);
    }

    [Fact]
    public async Task ScanStringAsync_WithJwt_DetectsLeak()
    {
        var jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abc123def456ghi789";
        var result = await _scanner.ScanStringAsync(jwt, DefaultScanContext, TestContext.Current.CancellationToken);

        result.HasLeaks.ShouldBeTrue();
        result.Findings.ShouldContain(f => f.PatternName == "jwt_token");
    }

    [Fact]
    public async Task ScanAsync_WithBytePayload_ScansCorrectly()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("AKIAIOSFODNN7EXAMPLE");
        var result = await _scanner.ScanAsync(bytes, DefaultScanContext, TestContext.Current.CancellationToken);

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

    // --- Multiple leaks in single input ---

    [Fact]
    public async Task ScanStringAsync_MultipleLeaks_FindsAll()
    {
        var content = """
            key1: AKIAIOSFODNN7EXAMPLE
            key2: ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghij
            """;

        var result = await _scanner.ScanStringAsync(content, DefaultScanContext, TestContext.Current.CancellationToken);

        result.HasLeaks.ShouldBeTrue();
        result.Findings.ShouldContain(f => f.PatternName == "aws_access_key");
        result.Findings.ShouldContain(f => f.PatternName == "github_token");
    }

    // --- Additional pattern coverage ---

    [Fact]
    public async Task ScanStringAsync_GitHubPat_DetectsLeak()
    {
        var content = "token: github_pat_ABCDEFGHIJ1234567890ab_ABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890";

        var result = await _scanner.ScanStringAsync(content, DefaultScanContext, TestContext.Current.CancellationToken);

        result.HasLeaks.ShouldBeTrue();
        result.Findings.ShouldContain(f => f.PatternName == "github_pat");
    }

    [Fact]
    public async Task ScanStringAsync_OpenAiKey_DetectsLeak()
    {
        var content = "sk-proj1234567890abcdefghij";

        var result = await _scanner.ScanStringAsync(content, DefaultScanContext, TestContext.Current.CancellationToken);

        result.HasLeaks.ShouldBeTrue();
        result.Findings.ShouldContain(f => f.PatternName == "openai_key");
    }

    [Fact]
    public async Task ScanStringAsync_SlackWebhook_DetectsLeak()
    {
        var content = "https://hooks.slack.com/services/T0123ABCD/B0123ABCD/xyzAbcDef123456789";

        var result = await _scanner.ScanStringAsync(content, DefaultScanContext, TestContext.Current.CancellationToken);

        result.HasLeaks.ShouldBeTrue();
        result.Findings.ShouldContain(f => f.PatternName == "slack_webhook");
    }

    [Fact]
    public async Task ScanStringAsync_GenericApiKey_DetectsLeak()
    {
        var content = """api_key = "ABCD1234EFGH5678IJKL9012" """;

        var result = await _scanner.ScanStringAsync(content, DefaultScanContext, TestContext.Current.CancellationToken);

        result.HasLeaks.ShouldBeTrue();
        result.Findings.ShouldContain(f => f.PatternName == "generic_api_key");
    }

    [Fact]
    public async Task ScanStringAsync_ConnectionString_DetectsLeak()
    {
        var content = """connection string = "Server=myserver;Database=mydb;User=admin;Password=s3cret1234567890" """;

        var result = await _scanner.ScanStringAsync(content, DefaultScanContext, TestContext.Current.CancellationToken);

        result.HasLeaks.ShouldBeTrue();
        result.Findings.ShouldContain(f => f.PatternName == "connection_string");
    }

    [Fact]
    public async Task ScanStringAsync_BasicAuth_DetectsLeak()
    {
        var content = "Authorization: Basic dXNlcm5hbWU6cGFzc3dvcmQ=";

        var result = await _scanner.ScanStringAsync(content, DefaultScanContext, TestContext.Current.CancellationToken);

        result.HasLeaks.ShouldBeTrue();
        result.Findings.ShouldContain(f => f.PatternName == "basic_auth");
    }

    [Fact]
    public async Task ScanStringAsync_AzureStorageKey_DetectsLeak()
    {
        var fakeKey = new string('A', 44) + "==";
        var content = $"AccountKey={fakeKey}";

        var result = await _scanner.ScanStringAsync(content, DefaultScanContext, TestContext.Current.CancellationToken);

        result.HasLeaks.ShouldBeTrue();
        result.Findings.ShouldContain(f => f.PatternName == "azure_storage_key");
    }

    [Fact]
    public async Task ScanStringAsync_PrivateKeyPemVariants_DetectsAll()
    {
        var content = """
            -----BEGIN EC PRIVATE KEY-----
            -----BEGIN OPENSSH PRIVATE KEY-----
            """;

        var result = await _scanner.ScanStringAsync(content, DefaultScanContext, TestContext.Current.CancellationToken);

        result.HasLeaks.ShouldBeTrue();
        result.Findings.Count(f => f.PatternName == "private_key_pem").ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task ScanStringAsync_AwsSecretKey_DetectsLeak()
    {
        var fakeKey = new string('A', 40);
        var content = $"AWS_SECRET_ACCESS_KEY={fakeKey}";

        var result = await _scanner.ScanStringAsync(content, DefaultScanContext, TestContext.Current.CancellationToken);

        result.HasLeaks.ShouldBeTrue();
        result.Findings.ShouldContain(f => f.PatternName == "aws_secret_key");
    }

    // --- High entropy detection ---

    [Fact]
    public async Task ScanStringAsync_HighEntropyToken_DetectedAsLeak()
    {
        // A random-looking 40 char token should trigger entropy detection
        var content = "token: aB3xZ9mK2pL5nQ8wR7tY6uI4oH1jF0gD2sA3dE";

        var result = await _scanner.ScanStringAsync(content, DefaultScanContext, TestContext.Current.CancellationToken);

        result.HasLeaks.ShouldBeTrue();
        result.Findings.ShouldContain(f => f.PatternName == "high_entropy_string");
    }

    [Fact]
    public async Task ScanStringAsync_RepetitiveString_NotFlaggedAsHighEntropy()
    {
        // Long but low-entropy string — should NOT trigger
        var content = new string('a', 50);

        var result = await _scanner.ScanStringAsync(content, DefaultScanContext, TestContext.Current.CancellationToken);

        result.Findings.ShouldNotContain(f => f.PatternName == "high_entropy_string");
    }

    // --- Shannon entropy edge cases ---

    [Fact]
    public void CalculateShannonEntropy_EmptyString_ReturnsZero()
    {
        LeakScanner.CalculateShannonEntropy("").ShouldBe(0.0);
    }

    [Fact]
    public void CalculateShannonEntropy_SingleChar_ReturnsZero()
    {
        LeakScanner.CalculateShannonEntropy("a").ShouldBe(0.0);
    }

    [Fact]
    public void CalculateShannonEntropy_TwoDistinctChars_ReturnsOne()
    {
        // "ab" has exactly 1 bit of entropy
        var entropy = LeakScanner.CalculateShannonEntropy("ab");
        entropy.ShouldBe(1.0, 0.001);
    }

    [Fact]
    public void CalculateShannonEntropy_NonAsciiChars_Handled()
    {
        var entropy = LeakScanner.CalculateShannonEntropy("héllo wörld café");
        entropy.ShouldBeGreaterThan(0.0);
    }

    // --- Cancellation ---

    [Fact]
    public async Task ScanStringAsync_CancelledToken_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => _scanner.ScanStringAsync("AKIAIOSFODNN7EXAMPLE", DefaultScanContext, cts.Token));
    }

    // --- Byte payload scanning ---

    [Fact]
    public async Task ScanAsync_EmptyPayload_ReturnsClean()
    {
        var result = await _scanner.ScanAsync(ReadOnlyMemory<byte>.Empty, DefaultScanContext, TestContext.Current.CancellationToken);

        result.HasLeaks.ShouldBeFalse();
    }

    // --- Finding offset and length ---

    [Fact]
    public async Task ScanStringAsync_FindingHasCorrectOffset()
    {
        var prefix = "safe text ";
        var secret = "AKIAIOSFODNN7EXAMPLE";
        var content = prefix + secret;

        var result = await _scanner.ScanStringAsync(content, DefaultScanContext, TestContext.Current.CancellationToken);

        var finding = result.Findings.First(f => f.PatternName == "aws_access_key");
        finding.Offset.ShouldBe(prefix.Length);
        finding.Length.ShouldBe(secret.Length);
    }
}
