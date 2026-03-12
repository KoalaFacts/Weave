using System.Text.Json;
using Weave.Shared.Secrets;

namespace Weave.Shared.Tests;

public sealed class SecretValueJsonConverterTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new SecretValueJsonConverter() }
    };

    [Fact]
    public void Write_AlwaysSerializesAsRedacted()
    {
        var secret = new SecretValue("sensitive-data");

        var json = JsonSerializer.Serialize(secret, Options);

        json.ShouldBe("\"***REDACTED***\"");
    }

    [Fact]
    public void Read_WithRedactedString_ReturnsDefault()
    {
        var json = "\"***REDACTED***\"";

        var result = JsonSerializer.Deserialize<SecretValue>(json, Options);

        result.HasValue.ShouldBeFalse();
    }

    [Fact]
    public void Read_WithEmptyString_ReturnsDefault()
    {
        var json = "\"\"";

        var result = JsonSerializer.Deserialize<SecretValue>(json, Options);

        result.HasValue.ShouldBeFalse();
    }

    [Fact]
    public void Read_WithNullString_ReturnsDefault()
    {
        var json = "null";

        var result = JsonSerializer.Deserialize<SecretValue>(json, Options);

        result.HasValue.ShouldBeFalse();
    }

    [Fact]
    public void Read_WithActualValue_CreatesSecretValue()
    {
        var json = "\"my-secret\"";

        var result = JsonSerializer.Deserialize<SecretValue>(json, Options);

        result.HasValue.ShouldBeTrue();
        result.DecryptToString().ShouldBe("my-secret");
    }

    [Fact]
    public void RoundTrip_AlwaysLosesValue()
    {
        var original = new SecretValue("original-value");

        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<SecretValue>(json, Options);

        // Serialization redacts, so deserialization cannot recover the value
        deserialized.HasValue.ShouldBeFalse();
    }
}
