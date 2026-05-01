using Weave.Shared.Secrets;

namespace Weave.Silo.Serialization;

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