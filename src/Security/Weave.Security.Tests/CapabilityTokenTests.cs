using Weave.Security.Tokens;

namespace Weave.Security.Tests;

public sealed class CapabilityTokenTests
{
    // --- HasGrant ---

    [Fact]
    public void HasGrant_ExactMatch_ReturnsTrue()
    {
        var token = new CapabilityToken { Grants = ["tool:my-tool", "secret:db-pass"] };

        token.HasGrant("tool:my-tool").ShouldBeTrue();
        token.HasGrant("secret:db-pass").ShouldBeTrue();
    }

    [Fact]
    public void HasGrant_NoMatch_ReturnsFalse()
    {
        var token = new CapabilityToken { Grants = ["tool:my-tool"] };

        token.HasGrant("tool:other-tool").ShouldBeFalse();
        token.HasGrant("secret:db-pass").ShouldBeFalse();
    }

    [Fact]
    public void HasGrant_WildcardGrant_MatchesAny()
    {
        var token = new CapabilityToken { Grants = ["*"] };

        token.HasGrant("tool:anything").ShouldBeTrue();
        token.HasGrant("secret:anything").ShouldBeTrue();
        token.HasGrant("").ShouldBeTrue();
    }

    [Fact]
    public void HasGrant_EmptyGrants_ReturnsFalse()
    {
        var token = new CapabilityToken { Grants = [] };

        token.HasGrant("tool:my-tool").ShouldBeFalse();
    }

    [Fact]
    public void HasGrant_PartialWildcard_DoesNotMatch()
    {
        // "tool:*" is NOT a wildcard match (only "*" is)
        var token = new CapabilityToken { Grants = ["tool:*"] };

        // "tool:*" literally matches "tool:*"
        token.HasGrant("tool:*").ShouldBeTrue();
        // but does NOT match "tool:my-tool" (no wildcard expansion)
        token.HasGrant("tool:my-tool").ShouldBeFalse();
    }

    // --- IsExpired ---

    [Fact]
    public void IsExpiredAt_NowBeforeExpiry_ReturnsFalse()
    {
        var now = new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero);
        var token = new CapabilityToken { ExpiresAt = now.AddHours(1) };

        token.IsExpiredAt(now).ShouldBeFalse();
    }

    [Fact]
    public void IsExpiredAt_NowAfterExpiry_ReturnsTrue()
    {
        var now = new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero);
        var token = new CapabilityToken { ExpiresAt = now.AddHours(-1) };

        token.IsExpiredAt(now).ShouldBeTrue();
    }

    [Fact]
    public void IsExpiredAt_DefaultExpiryIsEpoch_AnyNowIsExpired()
    {
        // default(DateTimeOffset) is 0001-01-01 — always in the past for any realistic "now".
        var token = new CapabilityToken();

        token.IsExpiredAt(DateTimeOffset.UnixEpoch).ShouldBeTrue();
    }

    // --- TokenId ---

    [Fact]
    public void TokenId_DefaultsToUniqueValue()
    {
        var token1 = new CapabilityToken();
        var token2 = new CapabilityToken();

        token1.TokenId.ShouldNotBe(token2.TokenId);
    }

    // --- CapabilityTokenRequest defaults ---

    [Fact]
    public void Request_DefaultLifetime_Is24Hours()
    {
        var request = new CapabilityTokenRequest();

        request.Lifetime.ShouldBe(TimeSpan.FromHours(24));
    }
}
