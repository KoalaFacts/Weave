using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Weave.Security.Tokens;

namespace Weave.Security.Tests;

public sealed class CapabilityRevocationFailureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "weave-revocation-failure-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero));
    private string Store => Path.Combine(_root, "revocations");
    private string SavedStore => Path.Combine(_root, "retained-revocations");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Validate_RevocationStoreUnavailable_DoesNotTreatMissingEvidenceAsPermission(bool replacedByFile)
    {
        var service = Service();
        var token = service.Mint(Request());
        service.Validate(token).ShouldBeTrue();
        Directory.Move(Store, SavedStore);
        if (replacedByFile)
            File.WriteAllText(Store, "not a directory");

        service.Validate(token).ShouldBeFalse();
        service.IsRevoked(token.TokenId).ShouldBeTrue();
        Directory.Exists(Store).ShouldBeFalse();
    }

    [Fact]
    public void Validate_DirectoryAtRevocationMarker_DoesNotTreatInvalidMarkerAsPermission()
    {
        var service = Service();
        var token = service.Mint(Request());
        Directory.CreateDirectory(Path.Combine(Store, token.TokenId + ".revoked"));

        service.Validate(token).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Revoke_PersistenceFails_ReportsFailureAndCancelsLocalLinkedToken(bool replacedByFile)
    {
        var service = Service();
        using var linked = service.MintLinked(Request(), TestContext.Current.CancellationToken);
        var observed = false;
        using var registration = linked.Token.CancellationToken.Register(() => observed = true);
        Directory.Move(Store, SavedStore);
        if (replacedByFile)
            File.WriteAllText(Store, "not a directory");

        var failure = Record.Exception(() => service.Revoke(linked.Token.TokenId));

        failure.ShouldNotBeNull();
        (failure is IOException or UnauthorizedAccessException).ShouldBeTrue();
        linked.Token.CancellationToken.IsCancellationRequested.ShouldBeTrue();
        observed.ShouldBeTrue();
        service.Validate(linked.Token).ShouldBeFalse();
        File.Exists(Path.Combine(SavedStore, linked.Token.TokenId + ".revoked")).ShouldBeFalse();
    }

    [Fact]
    public void Validate_HealthyEmptyStore_AllowsAuthenticUnrevokedToken()
    {
        var service = Service();
        var token = service.Mint(Request());

        Service().Validate(token).ShouldBeTrue();
        service.IsRevoked(token.TokenId).ShouldBeFalse();
    }

    [Fact]
    public void Revoke_PersistedMarker_IsSeenByAnotherVerifierAndAfterReconstruction()
    {
        var issuer = Service();
        var verifier = Service();
        using var linked = issuer.MintLinked(Request(), TestContext.Current.CancellationToken);
        verifier.Validate(linked.Token).ShouldBeTrue();

        issuer.Revoke(linked.Token.TokenId);

        linked.Token.CancellationToken.IsCancellationRequested.ShouldBeTrue();
        verifier.Validate(linked.Token).ShouldBeFalse();
        Service().Validate(linked.Token).ShouldBeFalse();
    }

    [Fact]
    public void Validate_OriginalStoreRestored_RecoversWithoutForgettingDurableRevocations()
    {
        var issuer = Service();
        var verifier = Service();
        var revoked = issuer.Mint(Request());
        var active = issuer.Mint(Request());
        issuer.Revoke(revoked.TokenId);
        Directory.Move(Store, SavedStore);

        verifier.Validate(active).ShouldBeFalse();
        verifier.Validate(revoked).ShouldBeFalse();
        Directory.Move(SavedStore, Store);

        verifier.Validate(active).ShouldBeTrue();
        verifier.Validate(revoked).ShouldBeFalse();
        Service().Validate(revoked).ShouldBeFalse();
    }

    [Fact]
    public void Validate_LocallyRevokedToken_StaysRejectedWhenTheStoreDisappears()
    {
        var service = Service();
        var token = service.Mint(Request());
        service.Revoke(token.TokenId);
        Directory.Move(Store, SavedStore);

        service.Validate(token).ShouldBeFalse();
    }

    private CapabilityTokenService Service() => new(Options.Create(new CapabilityTokenOptions
    {
        SigningKey = "test-revocation-key-" + "not-production-0123456789",
        RevocationDirectory = Store
    }), _clock);

    private static CapabilityTokenRequest Request() => new()
    {
        WorkspaceId = "workspace",
        IssuedTo = "agent",
        Grants = ["tool:files:invoke:write_file"],
        Lifetime = TimeSpan.FromHours(1)
    };

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
