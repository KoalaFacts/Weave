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
    public void IsExpired_FutureExpiry_ReturnsFalse()
    {
        var token = new CapabilityToken
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };

        token.IsExpired.ShouldBeFalse();
    }

    [Fact]
    public void IsExpired_PastExpiry_ReturnsTrue()
    {
        var token = new CapabilityToken
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1)
        };

        token.IsExpired.ShouldBeTrue();
    }

    [Fact]
    public void IsExpired_DefaultExpiry_IsExpired()
    {
        // Default ExpiresAt is DateTimeOffset.MinValue equivalent (default)
        var token = new CapabilityToken();

        // default(DateTimeOffset) is epoch (0001-01-01) which is always in the past
        token.IsExpired.ShouldBeTrue();
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
