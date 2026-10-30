using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Weave.Security.Tokens;

namespace Weave.Security.Tests;

public sealed class CapabilityTokenIntegrityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "weave-token-integrity-" + Guid.NewGuid().ToString("N"));
    private readonly CapabilityTokenService _service;

    public CapabilityTokenIntegrityTests()
    {
        _service = new CapabilityTokenService(Options.Create(new CapabilityTokenOptions
        {
            SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            RevocationDirectory = Path.Combine(_directory, "revocations")
        }), new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Validate_RepartitionedIdentityFields_ReturnsFalse()
    {
        var token = _service.Mint(Request() with { WorkspaceId = "acme:ops", IssuedTo = "agent" });

        _service.Validate(token with { WorkspaceId = "acme", IssuedTo = "ops:agent" }).ShouldBeFalse();
    }

    [Fact]
    public void Validate_RepartitionedGrantFields_ReturnsFalse()
    {
        var token = _service.Mint(Request() with { Grants = ["tool:read,tool:write"] });

        _service.Validate(token with { Grants = ["tool:read", "tool:write"] }).ShouldBeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Validate_TimestampChangedByOneTick_ReturnsFalse(bool changeExpiry)
    {
        var token = _service.Mint(Request());
        var tampered = changeExpiry
            ? token with { ExpiresAt = token.ExpiresAt.AddTicks(1) }
            : token with { IssuedAt = token.IssuedAt.AddTicks(1) };

        _service.Validate(tampered).ShouldBeFalse();
    }

    [Fact]
    public void Validate_CultureChangesBetweenIssuerAndVerifier_RemainsValid()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var token = _service.Mint(Request() with { Grants = ["tool:ä", "tool:z"] });
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");

            _service.Validate(token).ShouldBeTrue();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Mint_RequestGrantsChangedAfterIssuance_DoesNotChangeToken()
    {
        var request = Request();
        var token = _service.Mint(request);
        request.Grants.Add("tool:write");

        token.Grants.ShouldNotContain("tool:write");
        _service.Validate(token).ShouldBeTrue();
    }

    [Fact]
    public void Validate_MissingSignature_ReturnsFalse()
    {
        var token = _service.Mint(Request());

        _service.Validate(token with { Signature = null! }).ShouldBeFalse();
    }

    [Fact]
    public void Validate_MissingGrants_ReturnsFalse()
    {
        var token = _service.Mint(Request());

        _service.Validate(token with { Grants = null! }).ShouldBeFalse();
    }

    [Fact]
    public void Revoke_TraversalTokenId_DoesNotWriteOutsideRevocationDirectory()
    {
        Should.Throw<ArgumentException>(() => _service.Revoke("../outside"));

        File.Exists(Path.Combine(_directory, "outside.revoked")).ShouldBeFalse();
    }

    [Fact]
    public void IsRevoked_TraversalTokenId_DoesNotReadOutsideRevocationDirectory()
    {
        File.WriteAllText(Path.Combine(_directory, "outside.revoked"), "not a revocation");

        _service.IsRevoked("../outside").ShouldBeFalse();
    }

    [Fact]
    public void Validate_EquivalentUtcInstantWithDifferentOffset_RemainsValid()
    {
        var token = _service.Mint(Request());

        _service.Validate(token with
        {
            IssuedAt = token.IssuedAt.ToOffset(TimeSpan.FromHours(10)),
            ExpiresAt = token.ExpiresAt.ToOffset(TimeSpan.FromHours(10))
        }).ShouldBeTrue();
    }

    [Fact]
    public void Validate_SameGrantsInDifferentInsertionOrder_RemainsValid()
    {
        var token = _service.Mint(Request() with { Grants = ["tool:read", "tool:list"] });

        _service.Validate(token with { Grants = ["tool:list", "tool:read"] }).ShouldBeTrue();
    }

    private static CapabilityTokenRequest Request() => new()
    {
        WorkspaceId = "acme",
        IssuedTo = "agent",
        Grants = ["tool:read"],
        Lifetime = TimeSpan.FromHours(1)
    };

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }
}
