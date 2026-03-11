namespace Weave.Security.Tokens;

public interface ICapabilityTokenService
{
    CapabilityToken Mint(CapabilityTokenRequest request);
    bool Validate(CapabilityToken token);
    void Revoke(string tokenId);
    bool IsRevoked(string tokenId);
}
