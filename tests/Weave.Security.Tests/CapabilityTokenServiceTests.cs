using FluentAssertions;
using Weave.Security.Tokens;
using Xunit;

namespace Weave.Security.Tests;

public sealed class CapabilityTokenServiceTests
{
    private readonly CapabilityTokenService _service = new();

    [Fact]
    public void Mint_WithValidRequest_ReturnsSignedToken()
    {
        var request = new CapabilityTokenRequest
        {
            WorkspaceId = "test-workspace",
            IssuedTo = "agent-1",
            Grants = ["tool:web-search", "secret:*"],
            Lifetime = TimeSpan.FromHours(1)
        };

        var token = _service.Mint(request);

        token.WorkspaceId.Should().Be("test-workspace");
        token.IssuedTo.Should().Be("agent-1");
        token.Grants.Should().Contain("tool:web-search");
        token.Signature.Should().NotBeNullOrEmpty();
        token.IsExpired.Should().BeFalse();
    }

    [Fact]
    public void Validate_WithValidToken_ReturnsTrue()
    {
        var token = _service.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "ws",
            IssuedTo = "agent",
            Grants = ["*"],
            Lifetime = TimeSpan.FromHours(1)
        });

        _service.Validate(token).Should().BeTrue();
    }

    [Fact]
    public void Validate_WithExpiredToken_ReturnsFalse()
    {
        var token = _service.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "ws",
            IssuedTo = "agent",
            Grants = ["*"],
            Lifetime = TimeSpan.FromMilliseconds(-1)
        });

        _service.Validate(token).Should().BeFalse();
    }

    [Fact]
    public void Validate_WithRevokedToken_ReturnsFalse()
    {
        var token = _service.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "ws",
            IssuedTo = "agent",
            Grants = ["*"],
            Lifetime = TimeSpan.FromHours(1)
        });

        _service.Revoke(token.TokenId);

        _service.Validate(token).Should().BeFalse();
    }

    [Fact]
    public void Validate_WithTamperedSignature_ReturnsFalse()
    {
        var token = _service.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "ws",
            IssuedTo = "agent",
            Grants = ["*"],
            Lifetime = TimeSpan.FromHours(1)
        });

        var tampered = token with { Signature = "tampered" };

        _service.Validate(tampered).Should().BeFalse();
    }

    [Fact]
    public void HasGrant_WithWildcard_GrantsEverything()
    {
        var token = _service.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "ws",
            IssuedTo = "agent",
            Grants = ["*"],
            Lifetime = TimeSpan.FromHours(1)
        });

        token.HasGrant("tool:anything").Should().BeTrue();
        token.HasGrant("secret:anything").Should().BeTrue();
    }

    [Fact]
    public void HasGrant_WithSpecificGrant_OnlyMatchesExact()
    {
        var token = _service.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "ws",
            IssuedTo = "agent",
            Grants = ["tool:web-search"],
            Lifetime = TimeSpan.FromHours(1)
        });

        token.HasGrant("tool:web-search").Should().BeTrue();
        token.HasGrant("tool:github-api").Should().BeFalse();
    }
}
