using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Weave.Shared.Secrets;

/// <summary>
/// Authenticated-encryption envelope for secrets in-memory. The plaintext
/// never leaves this struct.
/// </summary>
/// <remarks>
/// External code (including Orleans surrogates, custom serializers, or
/// plugin-authored adapters) should round-trip <see cref="SecretValue"/>
/// via <see cref="ToEnvelope"/> / <see cref="FromEnvelope"/>, which expose
/// only the already-encrypted bytes.
/// </remarks>
[DebuggerDisplay("SecretValue(REDACTED)")]
[JsonConverter(typeof(SecretValueJsonConverter))]
public readonly struct SecretValue : IEquatable<SecretValue>, IDisposable
{
    private static readonly byte[] ProcessKey = RandomNumberGenerator.GetBytes(32);

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly byte[] _encrypted;

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly byte[] _nonce;

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly byte[] _tag;

    public SecretValue(ReadOnlySpan<byte> plaintext)
    {
        _nonce = RandomNumberGenerator.GetBytes(12);
        _encrypted = new byte[plaintext.Length];
        _tag = new byte[16];

        using var aes = new AesGcm(ProcessKey, 16);
        aes.Encrypt(_nonce, plaintext, _encrypted, _tag);
    }

    public SecretValue(string plaintext) : this(Encoding.UTF8.GetBytes(plaintext)) { }

    /// <summary>
    /// Rehydrates a SecretValue from an already-encrypted envelope.
    /// Use together with <see cref="ToEnvelope"/> to round-trip
    /// through a wire format. The ciphertext is bound to the
    /// originating process's key, so envelopes are not portable
    /// across hosts.
    /// </summary>
    public static SecretValue FromEnvelope(in Envelope envelope) =>
        new(envelope.Ciphertext ?? [], envelope.Nonce ?? [], envelope.Tag ?? []);

    private SecretValue(byte[] encrypted, byte[] nonce, byte[] tag)
    {
        _encrypted = encrypted;
        _nonce = nonce;
        _tag = tag;
    }

    public bool HasValue => _encrypted is { Length: > 0 };

    /// <summary>
    /// Returns a copy of the internal ciphertext envelope (ciphertext,
    /// nonce, authentication tag). Safe to serialize; the plaintext
    /// is never exposed.
    /// </summary>
    public Envelope ToEnvelope() => new()
    {
        Ciphertext = _encrypted,
        Nonce = _nonce,
        Tag = _tag
    };

    public byte[] Decrypt()
    {
        if (_encrypted is not { Length: > 0 })
            return [];

        var plaintext = new byte[_encrypted.Length];
        using var aes = new AesGcm(ProcessKey, 16);
        aes.Decrypt(_nonce, _encrypted, _tag, plaintext);
        return plaintext;
    }

    public string DecryptToString()
    {
        var bytes = Decrypt();
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public override string ToString() => "***REDACTED***";

    public bool Equals(SecretValue other) =>
        _encrypted is not null && other._encrypted is not null &&
        CryptographicOperations.FixedTimeEquals(_encrypted, other._encrypted);

    public override bool Equals(object? obj) => obj is SecretValue other && Equals(other);

    public override int GetHashCode() => _encrypted is not null
        ? HashCode.Combine(_encrypted.Length)
        : 0;

    public static bool operator ==(SecretValue left, SecretValue right) => left.Equals(right);
    public static bool operator !=(SecretValue left, SecretValue right) => !left.Equals(right);

    public void Dispose()
    {
        if (_encrypted is not null)
            CryptographicOperations.ZeroMemory(_encrypted);
    }

    /// <summary>
    /// Opaque encrypted representation of a <see cref="SecretValue"/>.
    /// All three byte arrays are required to reconstruct the original.
    /// </summary>
    public readonly struct Envelope
    {
        public required byte[] Ciphertext { get; init; }
        public required byte[] Nonce { get; init; }
        public required byte[] Tag { get; init; }
    }
}
