namespace Weave.Security.Tokens;

public interface ICapabilityTokenService
{
    CapabilityToken Mint(CapabilityTokenRequest request);

    /// <summary>
    /// Mints a token whose <see cref="CapabilityToken.CancellationToken"/> fires
    /// when any of: the parent <paramref name="parentCt"/> cancels, the token
    /// expires, or <see cref="Revoke"/> is called for this token's id.
    /// Caller owns the returned source and must dispose it.
    /// </summary>
    CapabilityTokenSource MintLinked(CapabilityTokenRequest request, CancellationToken parentCt);

    bool Validate(CapabilityToken token);
    void Revoke(string tokenId);
    bool IsRevoked(string tokenId);
}
