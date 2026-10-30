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
