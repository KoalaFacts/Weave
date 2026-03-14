using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weave.Shared.Secrets;

public sealed class SecretValueJsonConverter : JsonConverter<SecretValue>
{
    public override SecretValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        if (string.IsNullOrEmpty(value) || value == "***REDACTED***")
            return default;

        return new SecretValue(value);
    }

    public override void Write(Utf8JsonWriter writer, SecretValue value, JsonSerializerOptions options)
    {
        writer.WriteStringValue("***REDACTED***");
    }
}
