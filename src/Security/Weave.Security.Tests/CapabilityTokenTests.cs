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
    public void HasGrant_PrefixWildcard_MatchesScopedGrants()
    {
        var token = new CapabilityToken { Grants = ["tool:*"] };

        token.HasGrant("tool:*").ShouldBeTrue();
        token.HasGrant("tool:my-tool").ShouldBeTrue();
    }

    [Fact]
    public void HasGrant_NestedPrefixWildcard_MatchesDeeperScopes()
    {
        var token = new CapabilityToken { Grants = ["channel:send:*"] };

        token.HasGrant("channel:send:slack-1").ShouldBeTrue();
        token.HasGrant("channel:receive:slack-1").ShouldBeFalse();
    }

    [Fact]
    public void HasGrant_DifferentPrefix_DoesNotMatch()
    {
        var token = new CapabilityToken { Grants = ["channel:send:*"] };

        token.HasGrant("tool:my-tool").ShouldBeFalse();
    }

    [Fact]
    public void HasGrant_MidSegmentWildcard_MatchesAcrossThatSegment()
    {
        var token = new CapabilityToken { Grants = ["user:*:alice"] };

        token.HasGrant("user:read:alice").ShouldBeTrue();
        token.HasGrant("user:write:alice").ShouldBeTrue();
    }

    [Fact]
    public void HasGrant_MidSegmentWildcard_DoesNotMatchDifferentTrailingSegment()
    {
        var token = new CapabilityToken { Grants = ["user:*:alice"] };

        token.HasGrant("user:read:bob").ShouldBeFalse();
    }

    [Fact]
    public void HasGrant_MidSegmentWildcard_OnlyMatchesSameSegmentCount()
    {
        var token = new CapabilityToken { Grants = ["user:*:alice"] };

        token.HasGrant("user:read:alice:extra").ShouldBeFalse();
        token.HasGrant("user:alice").ShouldBeFalse();
    }

    [Fact]
    public void HasGrant_TrailingWildcard_MatchesAnyDepth()
    {
        var token = new CapabilityToken { Grants = ["tool:*"] };

        token.HasGrant("tool:foo").ShouldBeTrue();
        token.HasGrant("tool:foo:bar").ShouldBeTrue();
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

    // --- Exact expiry boundary ---

    [Fact]
    public void IsExpiredAt_ExactlyAtExpiry_ReturnsTrue()
    {
        var expiry = new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero);
        var token = new CapabilityToken { ExpiresAt = expiry };

        // now == ExpiresAt => expired (uses >=)
        token.IsExpiredAt(expiry).ShouldBeTrue();
    }

    [Fact]
    public void IsExpiredAt_OneTickBeforeExpiry_ReturnsFalse()
    {
        var expiry = new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero);
        var token = new CapabilityToken { ExpiresAt = expiry };

        token.IsExpiredAt(expiry.AddTicks(-1)).ShouldBeFalse();
    }

    // --- Default token ---

    [Fact]
    public void DefaultToken_HasEmptyFields()
    {
        var token = new CapabilityToken();

        token.WorkspaceId.ShouldBe(string.Empty);
        token.IssuedTo.ShouldBe(string.Empty);
        token.Grants.ShouldBeEmpty();
        token.Signature.ShouldBe(string.Empty);
        token.TokenId.ShouldNotBeNullOrEmpty();
    }

    // --- HasGrant edge cases ---

    [Fact]
    public void HasGrant_EmptyString_WithWildcard_ReturnsTrue()
    {
        var token = new CapabilityToken { Grants = ["*"] };

        token.HasGrant("").ShouldBeTrue();
    }

    [Fact]
    public void HasGrant_EmptyString_WithoutWildcard_ReturnsFalse()
    {
        var token = new CapabilityToken { Grants = ["tool:my-tool"] };

        token.HasGrant("").ShouldBeFalse();
    }

    [Fact]
    public void HasGrant_CaseSensitive_DoesNotMatchDifferentCase()
    {
        var token = new CapabilityToken { Grants = ["Tool:MyTool"] };

        token.HasGrant("tool:mytool").ShouldBeFalse();
        token.HasGrant("Tool:MyTool").ShouldBeTrue();
    }

    // --- Static HasGrant(IEnumerable<string>, string) — manifest-side wildcard match ---

    [Fact]
    public void HasGrant_Static_ExactMatch_ReturnsTrue()
    {
        string[] owned = ["skill:read", "skill:write"];

        CapabilityToken.HasGrant(owned, "skill:read").ShouldBeTrue();
        CapabilityToken.HasGrant(owned, "skill:write").ShouldBeTrue();
    }

    [Fact]
    public void HasGrant_Static_PrefixWildcard_MatchesScopedRequest()
    {
        // The bug this overload exists to fix: a manifest declaring `skill:*`
        // must grant `skill:read` and `skill:write` for the gate, the same
        // way a token does.
        string[] owned = ["skill:*"];

        CapabilityToken.HasGrant(owned, "skill:read").ShouldBeTrue();
        CapabilityToken.HasGrant(owned, "skill:write").ShouldBeTrue();
    }

    [Fact]
    public void HasGrant_Static_MidSegmentWildcard_MatchesAcrossSegment()
    {
        string[] owned = ["user:*:alice"];

        CapabilityToken.HasGrant(owned, "user:read:alice").ShouldBeTrue();
        CapabilityToken.HasGrant(owned, "user:write:alice").ShouldBeTrue();
        CapabilityToken.HasGrant(owned, "user:read:bob").ShouldBeFalse();
    }

    [Fact]
    public void HasGrant_Static_NoMatch_ReturnsFalse()
    {
        string[] owned = ["tool:git", "skill:read"];

        CapabilityToken.HasGrant(owned, "skill:write").ShouldBeFalse();
        CapabilityToken.HasGrant(owned, "tool:other").ShouldBeFalse();
    }

    [Fact]
    public void HasGrant_Static_EmptyOwned_ReturnsFalse()
    {
        CapabilityToken.HasGrant([], "skill:read").ShouldBeFalse();
    }

    [Fact]
    public void HasGrant_Static_AcceptsAnyEnumerable()
    {
        // IReadOnlyList<string> is what AgentDefinition.Capabilities exposes —
        // this overload must accept it without a copy.
        IReadOnlyList<string> owned = ["secret:*"];

        CapabilityToken.HasGrant(owned, "secret:db-pass").ShouldBeTrue();
    }
}
