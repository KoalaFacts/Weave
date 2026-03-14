using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Weave.Security.Tokens;

public sealed class CapabilityTokenService : ICapabilityTokenService
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _revokedTokens = new();
    private readonly byte[] _signingKey;
    private readonly string _revocationDirectory;

    public CapabilityTokenService() : this(Options.Create(new CapabilityTokenOptions()))
    {
    }

    public CapabilityTokenService(IOptions<CapabilityTokenOptions> options)
    {
        var resolved = options.Value ?? new CapabilityTokenOptions();
        var signingKey = string.IsNullOrWhiteSpace(resolved.SigningKey)
            ? "weave-development-signing-key-change-me"
            : resolved.SigningKey;
        _signingKey = SHA256.HashData(Encoding.UTF8.GetBytes(signingKey));
        _revocationDirectory = resolved.RevocationDirectory
            ?? Path.Combine(Path.GetTempPath(), "weave-capability-revocations");
        Directory.CreateDirectory(_revocationDirectory);
    }

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
        File.WriteAllText(GetRevocationPath(tokenId), DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    }

    public bool IsRevoked(string tokenId)
    {
        return _revokedTokens.ContainsKey(tokenId) || File.Exists(GetRevocationPath(tokenId));
    }

    private string ComputeSignature(CapabilityToken token)
    {
        var payload = $"{token.TokenId}:{token.WorkspaceId}:{token.IssuedTo}:{token.IssuedAt.ToUnixTimeSeconds()}:{token.ExpiresAt.ToUnixTimeSeconds()}:{string.Join(',', token.Grants.Order())}";
        var hash = HMACSHA256.HashData(_signingKey, Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash);
    }

    private string GetRevocationPath(string tokenId) => Path.Combine(_revocationDirectory, $"{tokenId}.revoked");
}
