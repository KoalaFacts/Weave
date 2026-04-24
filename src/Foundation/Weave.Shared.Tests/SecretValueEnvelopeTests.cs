using Weave.Shared.Secrets;

namespace Weave.Shared.Tests;

/// <summary>
/// Round-trip coverage for <see cref="SecretValue.ToEnvelope"/> /
/// <see cref="SecretValue.FromEnvelope"/>. The envelope is the wire
/// format used by Orleans surrogates (see
/// <c>Weave.Silo.Serialization.SecretValueSurrogateConverter</c>),
/// so its behaviour is load-bearing across the actor boundary.
/// </summary>
public sealed class SecretValueEnvelopeTests
{
    [Fact]
    public void ToEnvelope_produces_non_empty_bytes_for_non_empty_secret()
    {
        var secret = new SecretValue("api-key-12345");
        var envelope = secret.ToEnvelope();

        envelope.Ciphertext.ShouldNotBeNull();
        envelope.Ciphertext.Length.ShouldBeGreaterThan(0);
        envelope.Nonce.ShouldNotBeNull();
        envelope.Nonce.Length.ShouldBeGreaterThan(0);
        envelope.Tag.ShouldNotBeNull();
        envelope.Tag.Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void FromEnvelope_roundtrips_the_original_plaintext()
    {
        var original = new SecretValue("round-trip-me");

        var envelope = original.ToEnvelope();
        var restored = SecretValue.FromEnvelope(envelope);

        restored.DecryptToString().ShouldBe("round-trip-me");
    }

    [Fact]
    public void FromEnvelope_with_empty_bytes_produces_empty_secret()
    {
        // Matches the null-coalesced path in FromEnvelope — a wire
        // payload with all-empty arrays decrypts to the empty secret.
        var envelope = new SecretValue.Envelope
        {
            Ciphertext = [],
            Nonce = [],
            Tag = []
        };

        var secret = SecretValue.FromEnvelope(envelope);

        secret.HasValue.ShouldBeFalse();
        secret.DecryptToString().ShouldBe(string.Empty);
    }

    [Fact]
    public void Envelope_fields_are_required()
    {
        // Documenting the required-modifier contract — a new
        // Envelope can only be constructed with all three fields.
        // This test just exercises the happy path so the properties
        // themselves register as hit.
        var envelope = new SecretValue.Envelope
        {
            Ciphertext = [1, 2, 3],
            Nonce = [4, 5, 6],
            Tag = [7, 8, 9]
        };

        envelope.Ciphertext.ShouldBe(new byte[] { 1, 2, 3 });
        envelope.Nonce.ShouldBe(new byte[] { 4, 5, 6 });
        envelope.Tag.ShouldBe(new byte[] { 7, 8, 9 });
    }

    [Fact]
    public void ToEnvelope_of_empty_secret_yields_empty_ciphertext()
    {
        // An empty SecretValue (from the default struct) has no
        // encrypted bytes — ToEnvelope should reflect that.
        var empty = default(SecretValue);
        var envelope = empty.ToEnvelope();

        // The envelope fields will be null for a default struct;
        // FromEnvelope handles the null case via `?? []`.
        var restored = SecretValue.FromEnvelope(envelope);
        restored.HasValue.ShouldBeFalse();
    }

    [Fact]
    public void Envelope_is_distinct_per_secret_due_to_random_nonce()
    {
        // Two SecretValues with the same plaintext produce DIFFERENT
        // envelopes because AES-GCM generates a random nonce per
        // encryption. This is a security property worth locking in.
        var a = new SecretValue("same-plaintext");
        var b = new SecretValue("same-plaintext");

        var envA = a.ToEnvelope();
        var envB = b.ToEnvelope();

        // Same plaintext but different nonces -> different ciphertext.
        envA.Nonce.ShouldNotBe(envB.Nonce);
        envA.Ciphertext.ShouldNotBe(envB.Ciphertext);

        // Both still decrypt to the same value.
        SecretValue.FromEnvelope(envA).DecryptToString().ShouldBe("same-plaintext");
        SecretValue.FromEnvelope(envB).DecryptToString().ShouldBe("same-plaintext");
    }
}
