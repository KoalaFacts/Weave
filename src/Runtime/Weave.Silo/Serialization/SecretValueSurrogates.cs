using Weave.Shared.Secrets;

namespace Weave.Silo.Serialization;

// ── SecretValue (authenticated ciphertext envelope) ─────────────
//
// Routes through the public Envelope API instead of internal fields.
// Any external plugin can adopt the same pattern for their own
// Orleans / custom-serializer adapters — no privileged access
// needed. The plaintext never exits SecretValue.

[GenerateSerializer]
public struct SecretValueSurrogate
{
    [Id(0)] public byte[] Ciphertext { get; set; }
    [Id(1)] public byte[] Nonce { get; set; }
    [Id(2)] public byte[] Tag { get; set; }
}

[RegisterConverter]
public sealed class SecretValueSurrogateConverter
    : IConverter<SecretValue, SecretValueSurrogate>
{
    public SecretValue ConvertFromSurrogate(in SecretValueSurrogate s) =>
        SecretValue.FromEnvelope(new SecretValue.Envelope
        {
            Ciphertext = s.Ciphertext ?? [],
            Nonce = s.Nonce ?? [],
            Tag = s.Tag ?? []
        });

    public SecretValueSurrogate ConvertToSurrogate(in SecretValue v)
    {
        var env = v.ToEnvelope();
        return new SecretValueSurrogate
        {
            Ciphertext = env.Ciphertext,
            Nonce = env.Nonce,
            Tag = env.Tag
        };
    }
}
