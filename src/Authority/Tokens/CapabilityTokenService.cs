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
    private readonly byte[]? _previousSigningKey;
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

        if (!string.IsNullOrWhiteSpace(resolved.PreviousSigningKey))
        {
            if (resolved.PreviousSigningKey.Length < CapabilityTokenOptions.MinimumSigningKeyLength)
                throw new InvalidOperationException(
                    $"CapabilityTokens:PreviousSigningKey must be at least {CapabilityTokenOptions.MinimumSigningKeyLength} characters. " +
                    $"Current length: {resolved.PreviousSigningKey.Length}.");

            _previousSigningKey = SHA256.HashData(Encoding.UTF8.GetBytes(resolved.PreviousSigningKey));
        }

        _revocationDirectory = resolved.RevocationDirectory
            ?? Path.Combine(Path.GetTempPath(), "weave-capability-revocations");
        if (resolved.RequireExistingStorage)
        {
            if ((File.GetAttributes(_revocationDirectory) & FileAttributes.Directory) == 0)
                throw new InvalidOperationException("Recovery requires an existing revocation directory.");
        }
        else
            Directory.CreateDirectory(_revocationDirectory);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public CapabilityToken Mint(CapabilityTokenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Grants);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IssuedTo);

        var now = _timeProvider.GetUtcNow();
        var token = new CapabilityToken
        {
            WorkspaceId = request.WorkspaceId,
            IssuedTo = request.IssuedTo,
            Grants = new HashSet<string>(request.Grants, StringComparer.Ordinal),
            IssuedAt = now,
            ExpiresAt = now.Add(request.Lifetime)
        };

        if (!CapabilityTokenPayload.HasValidFields(token))
            throw new ArgumentException("Token fields exceed the supported limits or contain invalid values.", nameof(request));

        var signature = ComputeSignature(token, _signingKey);
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
        if (!CapabilityTokenPayload.HasValidFields(token)
            || token.Signature is not { Length: 44 }
            || token.ExpiresAt <= _timeProvider.GetUtcNow())
            return false;

        Span<byte> presented = stackalloc byte[32];
        if (!Convert.TryFromBase64String(token.Signature, presented, out var written) || written != presented.Length)
            return false;

        byte[] payload;
        try
        {
            payload = CapabilityTokenPayload.Encode(token);
        }
        catch (EncoderFallbackException)
        {
            // Ill-formed untrusted Unicode is an invalid token, not replacement text to authenticate.
            return false;
        }

        var authenticated = SignatureMatches(payload, _signingKey, presented)
            || (_previousSigningKey is not null && SignatureMatches(payload, _previousSigningKey, presented));
        return authenticated && !IsRevoked(token.TokenId);
    }

    private static bool SignatureMatches(byte[] payload, byte[] key, ReadOnlySpan<byte> presented)
    {
        var expected = HMACSHA256.HashData(key, payload);
        return CryptographicOperations.FixedTimeEquals(presented, expected);
    }

    public void Revoke(string tokenId)
    {
        if (!CapabilityTokenPayload.HasValidTokenId(tokenId))
            throw new ArgumentException("A canonical token identifier is required.", nameof(tokenId));

        var now = _timeProvider.GetUtcNow();
        _revokedTokens.TryAdd(tokenId, now);
        Exception? persistenceFailure = null;
        try
        {
            try
            {
                File.WriteAllText(GetRevocationPath(tokenId), now.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                persistenceFailure = error;
                throw;
            }
            finally
            {
                // Persistence failure cannot bypass local cancellation.
                if (_liveSources.TryRemove(tokenId, out var cts))
                {
                    try
                    {
                        cts.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // source already disposed
                    }
                }
            }
        }
        catch (AggregateException cancellationFailure) when (persistenceFailure is not null)
        {
            throw new AggregateException("Revocation persistence and cancellation callbacks both failed.",
                persistenceFailure, cancellationFailure);
        }
    }

    public bool IsRevoked(string tokenId)
    {
        if (!CapabilityTokenPayload.HasValidTokenId(tokenId))
            return false;
        if (_revokedTokens.ContainsKey(tokenId))
            return true;

        try
        {
            try
            {
                // Any entry at the marker path blocks use, including an unexpected directory.
                _ = File.GetAttributes(GetRevocationPath(tokenId));
                return true;
            }
            catch (FileNotFoundException)
            {
                // Absence is authoritative only while the parent store remains a directory.
                return (File.GetAttributes(_revocationDirectory) & FileAttributes.Directory) == 0;
            }
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string ComputeSignature(CapabilityToken token, byte[] key)
    {
        var hash = HMACSHA256.HashData(key, CapabilityTokenPayload.Encode(token));
        return Convert.ToBase64String(hash);
    }

    private string GetRevocationPath(string tokenId) => Path.Combine(_revocationDirectory, $"{tokenId}.revoked");
}
