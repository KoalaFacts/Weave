using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Orleans;

namespace Weave.Shared.Secrets;

[GenerateSerializer]
[DebuggerDisplay("SecretValue(REDACTED)")]
[JsonConverter(typeof(SecretValueJsonConverter))]
public readonly struct SecretValue : IEquatable<SecretValue>, IDisposable
{
    private static readonly byte[] ProcessKey = RandomNumberGenerator.GetBytes(32);

    [Id(0)]
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly byte[] _encrypted;

    [Id(1)]
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly byte[] _nonce;

    [Id(2)]
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

    public bool HasValue => _encrypted is { Length: > 0 };

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
}
