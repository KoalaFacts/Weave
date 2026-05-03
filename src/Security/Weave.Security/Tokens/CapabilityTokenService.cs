using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Weave.Security.Tokens;

public sealed class CapabilityTokenService : ICapabilityTokenService
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _revokedTokens = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _liveSources = new();
    private readonly byte[] _signingKey;
    private readonly string _revocationDirectory;
    private readonly TimeProvider _timeProvider;

    public CapabilityTokenService(IOptions<CapabilityTokenOptions> options, TimeProvider timeProvider)
    {
        var resolved = options.Value ?? throw new ArgumentNullException(nameof(options), "CapabilityTokenOptions must be configured.");

        if (string.IsNullOrWhiteSpace(resolved.SigningKey))
            throw new InvalidOperationException(
                $"CapabilityTokens:SigningKey must be configured. " +
                $"Set it via configuration, environment variable, or secret store.");

        if (resolved.SigningKey.Length < CapabilityTokenOptions.MinimumSigningKeyLength)
            throw new InvalidOperationException(
                $"CapabilityTokens:SigningKey must be at least {CapabilityTokenOptions.MinimumSigningKeyLength} characters. " +
                $"Current length: {resolved.SigningKey.Length}.");

        _signingKey = SHA256.HashData(Encoding.UTF8.GetBytes(resolved.SigningKey));
        _revocationDirectory = resolved.RevocationDirectory
            ?? Path.Combine(Path.GetTempPath(), "weave-capability-revocations");
        Directory.CreateDirectory(_revocationDirectory);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public CapabilityToken Mint(CapabilityTokenRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IssuedTo);

        var now = _timeProvider.GetUtcNow();
        var token = new CapabilityToken
        {
            WorkspaceId = request.WorkspaceId,
            IssuedTo = request.IssuedTo,
            Grants = request.Grants,
            IssuedAt = now,
            ExpiresAt = now.Add(request.Lifetime)
        };

        var signature = ComputeSignature(token);
        return token with { Signature = signature };
    }

    public CapabilityTokenSource MintLinked(CapabilityTokenRequest request, CancellationToken parentCt)
    {
        var token = Mint(request);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);

        var remaining = token.ExpiresAt - _timeProvider.GetUtcNow();
        if (remaining > TimeSpan.Zero)
            cts.CancelAfter(remaining);
        else
            cts.Cancel();

        _liveSources[token.TokenId] = cts;
        return new CapabilityTokenSource(token, cts, () => _liveSources.TryRemove(token.TokenId, out _));
    }

    public bool Validate(CapabilityToken token)
    {
        if (token.ExpiresAt <= _timeProvider.GetUtcNow())
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
        var now = _timeProvider.GetUtcNow();
        _revokedTokens.TryAdd(tokenId, now);
        File.WriteAllText(GetRevocationPath(tokenId), now.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

        if (_liveSources.TryRemove(tokenId, out var cts))
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { /* source already disposed */ }
        }
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
