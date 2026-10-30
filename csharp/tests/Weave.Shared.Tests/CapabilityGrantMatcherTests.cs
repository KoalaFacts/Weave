using Weave.Shared.Capabilities;

namespace Weave.Shared.Tests;

public sealed class CapabilityGrantMatcherTests
{
    [Fact]
    public void HasGrant_ExactMatch_ReturnsTrue()
    {
        string[] owned = ["skill:read", "skill:write"];

        CapabilityGrantMatcher.HasGrant(owned, "skill:read").ShouldBeTrue();
        CapabilityGrantMatcher.HasGrant(owned, "skill:write").ShouldBeTrue();
    }

    [Fact]
    public void HasGrant_TrailingWildcard_MatchesScopedRequest()
    {
        // The bug this overload exists to fix: a manifest declaring `skill:*`
        // must grant `skill:read` and `skill:write` for the gate, the same
        // way a token does.
        string[] owned = ["skill:*"];

        CapabilityGrantMatcher.HasGrant(owned, "skill:read").ShouldBeTrue();
        CapabilityGrantMatcher.HasGrant(owned, "skill:write").ShouldBeTrue();
    }

    [Fact]
    public void HasGrant_TrailingWildcard_MatchesDeeperRequest()
    {
        string[] owned = ["tool:*"];

        CapabilityGrantMatcher.HasGrant(owned, "tool:foo").ShouldBeTrue();
        CapabilityGrantMatcher.HasGrant(owned, "tool:foo:bar").ShouldBeTrue();
    }

    [Fact]
    public void HasGrant_MidSegmentWildcard_MatchesAcrossSegment()
    {
        string[] owned = ["user:*:alice"];

        CapabilityGrantMatcher.HasGrant(owned, "user:read:alice").ShouldBeTrue();
        CapabilityGrantMatcher.HasGrant(owned, "user:write:alice").ShouldBeTrue();
        CapabilityGrantMatcher.HasGrant(owned, "user:read:bob").ShouldBeFalse();
    }

    [Fact]
    public void HasGrant_BareWildcard_MatchesAnything()
    {
        string[] owned = ["*"];

        CapabilityGrantMatcher.HasGrant(owned, "tool:git").ShouldBeTrue();
        CapabilityGrantMatcher.HasGrant(owned, "skill:read").ShouldBeTrue();
        CapabilityGrantMatcher.HasGrant(owned, "anything").ShouldBeTrue();
    }

    [Fact]
    public void HasGrant_NoMatch_ReturnsFalse()
    {
        string[] owned = ["tool:git", "skill:read"];

        CapabilityGrantMatcher.HasGrant(owned, "skill:write").ShouldBeFalse();
        CapabilityGrantMatcher.HasGrant(owned, "tool:other").ShouldBeFalse();
    }

    [Fact]
    public void HasGrant_EmptyOwned_ReturnsFalse()
    {
        CapabilityGrantMatcher.HasGrant([], "skill:read").ShouldBeFalse();
    }

    [Fact]
    public void HasGrant_AcceptsAnyEnumerable()
    {
        // IReadOnlyList<string> is what AgentDefinition.Capabilities exposes —
        // this overload must accept it without a copy.
        IReadOnlyList<string> owned = ["secret:*"];

        CapabilityGrantMatcher.HasGrant(owned, "secret:db-pass").ShouldBeTrue();
    }
}
