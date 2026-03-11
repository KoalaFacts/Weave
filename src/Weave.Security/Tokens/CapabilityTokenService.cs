using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Weave.Security.Tokens;

public sealed class CapabilityTokenService : ICapabilityTokenService
{
    private static readonly byte[] SigningKey = RandomNumberGenerator.GetBytes(32);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _revokedTokens = new();

    public CapabilityToken Mint(CapabilityTokenRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IssuedTo);

        var token = new CapabilityToken
        {
            WorkspaceId = request.WorkspaceId,
            IssuedTo = request.IssuedTo,
            Grants = request.Grants,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.Add(request.Lifetime)
        };

        var signature = ComputeSignature(token);
        return token with { Signature = signature };
    }

    public bool Validate(CapabilityToken token)
    {
        if (token.IsExpired)
            return false;

        if (IsRevoked(token.TokenId))
            return false;

        var expected = ComputeSignature(token);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(token.Signature),
            Encoding.UTF8.GetBytes(expected));
    }

    public void Revoke(string tokenId)
    {
        _revokedTokens.TryAdd(tokenId, DateTimeOffset.UtcNow);
    }

    public bool IsRevoked(string tokenId)
    {
        return _revokedTokens.ContainsKey(tokenId);
    }

    private static string ComputeSignature(CapabilityToken token)
    {
        var payload = $"{token.TokenId}:{token.WorkspaceId}:{token.IssuedTo}:{token.IssuedAt.ToUnixTimeSeconds()}:{token.ExpiresAt.ToUnixTimeSeconds()}:{string.Join(',', token.Grants.Order())}";
        var hash = HMACSHA256.HashData(SigningKey, Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash);
    }
}
