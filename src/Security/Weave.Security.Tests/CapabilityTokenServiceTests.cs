using Weave.Security.Tokens;

namespace Weave.Security.Tests;

public sealed class CapabilityTokenServiceTests
{
    private static CapabilityTokenService CreateService(string signingKey = "test-signing-key-that-is-at-least-32-chars-long") =>
        new(Microsoft.Extensions.Options.Options.Create(
            new CapabilityTokenOptions { SigningKey = signingKey }));

    private readonly CapabilityTokenService _service = CreateService();

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

        token.WorkspaceId.ShouldBe("test-workspace");
        token.IssuedTo.ShouldBe("agent-1");
        token.Grants.ShouldContain("tool:web-search");
        token.Signature.ShouldNotBeNullOrEmpty();
        token.IsExpired.ShouldBeFalse();
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

        _service.Validate(token).ShouldBeTrue();
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

        _service.Validate(token).ShouldBeFalse();
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

        _service.Validate(token).ShouldBeFalse();
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

        _service.Validate(tampered).ShouldBeFalse();
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

        token.HasGrant("tool:anything").ShouldBeTrue();
        token.HasGrant("secret:anything").ShouldBeTrue();
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

        token.HasGrant("tool:web-search").ShouldBeTrue();
        token.HasGrant("tool:github-api").ShouldBeFalse();
    }

    [Fact]
    public void Mint_WithEmptyWorkspaceId_Throws()
    {
        var act = () => _service.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "",
            IssuedTo = "agent",
            Grants = ["*"],
            Lifetime = TimeSpan.FromHours(1)
        });

        Should.Throw<ArgumentException>(act);
    }

    [Fact]
    public void Mint_WithEmptyIssuedTo_Throws()
    {
        var act = () => _service.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "ws",
            IssuedTo = "",
            Grants = ["*"],
            Lifetime = TimeSpan.FromHours(1)
        });

        Should.Throw<ArgumentException>(act);
    }

    [Fact]
    public void IsRevoked_WithNonRevokedToken_ReturnsFalse()
    {
        _service.IsRevoked("non-existent-id").ShouldBeFalse();
    }

    [Fact]
    public void IsRevoked_AfterRevoke_ReturnsTrue()
    {
        var token = _service.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "ws",
            IssuedTo = "agent",
            Grants = ["*"],
            Lifetime = TimeSpan.FromHours(1)
        });

        _service.Revoke(token.TokenId);

        _service.IsRevoked(token.TokenId).ShouldBeTrue();
    }

    [Fact]
    public void HasGrant_WithMultipleGrants_MatchesAny()
    {
        var token = _service.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "ws",
            IssuedTo = "agent",
            Grants = ["tool:web-search", "secret:api_key", "tool:code-search"],
            Lifetime = TimeSpan.FromHours(1)
        });

        token.HasGrant("tool:web-search").ShouldBeTrue();
        token.HasGrant("secret:api_key").ShouldBeTrue();
        token.HasGrant("tool:code-search").ShouldBeTrue();
        token.HasGrant("tool:github-api").ShouldBeFalse();
        token.HasGrant("secret:other").ShouldBeFalse();
    }

    [Fact]
    public void Mint_SetsCorrectExpiration()
    {
        var before = DateTimeOffset.UtcNow;
        var token = _service.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "ws",
            IssuedTo = "agent",
            Grants = ["*"],
            Lifetime = TimeSpan.FromHours(2)
        });
        var after = DateTimeOffset.UtcNow;

        token.ExpiresAt.ShouldBeGreaterThan(before.AddHours(2).AddSeconds(-1));
        token.ExpiresAt.ShouldBeLessThanOrEqualTo(after.AddHours(2));
        token.IssuedAt.ShouldBeGreaterThanOrEqualTo(before);
        token.IssuedAt.ShouldBeLessThanOrEqualTo(after);
    }

    [Fact]
    public void Validate_WithDifferentInstances_SameData_ValidatesCorrectly()
    {
        var token1 = _service.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "ws",
            IssuedTo = "agent",
            Grants = ["*"],
            Lifetime = TimeSpan.FromHours(1)
        });

        var token2 = _service.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "ws",
            IssuedTo = "agent",
            Grants = ["*"],
            Lifetime = TimeSpan.FromHours(1)
        });

        _service.Validate(token1).ShouldBeTrue();
        _service.Validate(token2).ShouldBeTrue();
        token1.TokenId.ShouldNotBe(token2.TokenId);
    }

    // --- Signature tampering variants ---

    [Fact]
    public void Validate_TamperedWorkspaceId_ReturnsFalse()
    {
        var token = _service.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "ws-1",
            IssuedTo = "agent",
            Grants = ["*"],
            Lifetime = TimeSpan.FromHours(1)
        });

        var tampered = token with { WorkspaceId = "ws-2" };

        _service.Validate(tampered).ShouldBeFalse();
    }

    [Fact]
    public void Validate_TamperedIssuedTo_ReturnsFalse()
    {
        var token = _service.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "ws",
            IssuedTo = "agent-1",
            Grants = ["*"],
            Lifetime = TimeSpan.FromHours(1)
        });

        var tampered = token with { IssuedTo = "agent-2" };

        _service.Validate(tampered).ShouldBeFalse();
    }

    [Fact]
    public void Validate_TamperedGrants_ReturnsFalse()
    {
        var token = _service.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "ws",
            IssuedTo = "agent",
            Grants = ["tool:read"],
            Lifetime = TimeSpan.FromHours(1)
        });

        var tampered = token with { Grants = ["tool:admin"] };

        _service.Validate(tampered).ShouldBeFalse();
    }

    [Fact]
    public void Validate_TamperedExpiresAt_ReturnsFalse()
    {
        var token = _service.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "ws",
            IssuedTo = "agent",
            Grants = ["*"],
            Lifetime = TimeSpan.FromHours(1)
        });

        // Extend expiration — signature should no longer match
        var tampered = token with { ExpiresAt = token.ExpiresAt.AddYears(1) };

        _service.Validate(tampered).ShouldBeFalse();
    }

    // --- Revocation ---

    [Fact]
    public void Revoke_ThenMintNew_OldRevokedNewValid()
    {
        var old = _service.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "ws",
            IssuedTo = "agent",
            Grants = ["*"],
            Lifetime = TimeSpan.FromHours(1)
        });

        _service.Revoke(old.TokenId);

        var fresh = _service.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "ws",
            IssuedTo = "agent",
            Grants = ["*"],
            Lifetime = TimeSpan.FromHours(1)
        });

        _service.Validate(old).ShouldBeFalse();
        _service.Validate(fresh).ShouldBeTrue();
    }

    [Fact]
    public void Revoke_SameTokenTwice_DoesNotThrow()
    {
        var token = _service.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "ws",
            IssuedTo = "agent",
            Grants = ["*"],
            Lifetime = TimeSpan.FromHours(1)
        });

        _service.Revoke(token.TokenId);
        Should.NotThrow(() => _service.Revoke(token.TokenId));
    }

    // --- Mint with empty grants ---

    [Fact]
    public void Mint_WithEmptyGrants_Succeeds()
    {
        var token = _service.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "ws",
            IssuedTo = "agent",
            Grants = [],
            Lifetime = TimeSpan.FromHours(1)
        });

        token.Grants.ShouldBeEmpty();
        _service.Validate(token).ShouldBeTrue();
        token.HasGrant("anything").ShouldBeFalse();
    }

    // --- Custom signing key ---

    [Fact]
    public void Validate_DifferentSigningKey_ReturnsFalse()
    {
        var service1 = CreateService("signing-key-one-that-is-at-least-32-characters");
        var service2 = CreateService("signing-key-two-that-is-at-least-32-characters");

        var token = service1.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "ws",
            IssuedTo = "agent",
            Grants = ["*"],
            Lifetime = TimeSpan.FromHours(1)
        });

        service1.Validate(token).ShouldBeTrue();
        service2.Validate(token).ShouldBeFalse();
    }

    // --- Constructor validation ---

    [Fact]
    public void Constructor_NullSigningKey_Throws()
    {
        var act = () => new CapabilityTokenService(
            Microsoft.Extensions.Options.Options.Create(
                new CapabilityTokenOptions { SigningKey = null }));

        Should.Throw<InvalidOperationException>(act)
            .Message.ShouldContain("must be configured");
    }

    [Fact]
    public void Constructor_EmptySigningKey_Throws()
    {
        var act = () => new CapabilityTokenService(
            Microsoft.Extensions.Options.Options.Create(
                new CapabilityTokenOptions { SigningKey = "" }));

        Should.Throw<InvalidOperationException>(act)
            .Message.ShouldContain("must be configured");
    }

    [Fact]
    public void Constructor_ShortSigningKey_Throws()
    {
        var act = () => new CapabilityTokenService(
            Microsoft.Extensions.Options.Options.Create(
                new CapabilityTokenOptions { SigningKey = "too-short" }));

        Should.Throw<InvalidOperationException>(act)
            .Message.ShouldContain("at least");
    }
}
