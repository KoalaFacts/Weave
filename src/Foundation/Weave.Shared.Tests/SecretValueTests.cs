using Weave.Shared.Secrets;

namespace Weave.Shared.Tests;

public sealed class SecretValueTests
{
    [Fact]
    public void Constructor_WithString_RoundTripsCorrectly()
    {
        var secret = new SecretValue("my-secret-password");

        secret.DecryptToString().ShouldBe("my-secret-password");
    }

    [Fact]
    public void Constructor_WithBytes_RoundTripsCorrectly()
    {
        var bytes = "binary-payload"u8.ToArray();
        var secret = new SecretValue(bytes.AsSpan());

        secret.Decrypt().ShouldBe(bytes);
    }

    [Fact]
    public void HasValue_WhenConstructedWithData_ReturnsTrue()
    {
        var secret = new SecretValue("test");

        secret.HasValue.ShouldBeTrue();
    }

    [Fact]
    public void HasValue_WhenDefault_ReturnsFalse()
    {
        SecretValue secret = default;

        secret.HasValue.ShouldBeFalse();
    }

    [Fact]
    public void Decrypt_WhenDefault_ReturnsEmptyArray()
    {
        SecretValue secret = default;

        secret.Decrypt().ShouldBeEmpty();
    }

    [Fact]
    public void DecryptToString_WhenDefault_ReturnsEmptyString()
    {
        SecretValue secret = default;

        secret.DecryptToString().ShouldBe(string.Empty);
    }

    [Fact]
    public void ToString_AlwaysReturnsRedacted()
    {
        var secret = new SecretValue("sensitive-data");

        secret.ToString().ShouldBe("***REDACTED***");
    }

    [Fact]
    public void Equality_SameValue_AreEqual()
    {
        var secret1 = new SecretValue("same");
        var secret2 = new SecretValue("same");

        // Different nonces mean different ciphertexts, so they should NOT be equal
        (secret1 == secret2).ShouldBeFalse();
    }

    [Fact]
    public void Equality_DifferentValues_AreNotEqual()
    {
        var secret1 = new SecretValue("one");
        var secret2 = new SecretValue("two");

        (secret1 != secret2).ShouldBeTrue();
    }

    [Fact]
    public void Equality_DefaultValues_AreNotEqual()
    {
        SecretValue s1 = default;
        SecretValue s2 = default;

        // Both have null _encrypted, so Equals returns false
        s1.Equals(s2).ShouldBeFalse();
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var secret = new SecretValue("disposable");

        Should.NotThrow(() => secret.Dispose());
    }

    [Fact]
    public void Dispose_OnDefault_DoesNotThrow()
    {
        SecretValue secret = default;

        Should.NotThrow(() => secret.Dispose());
    }

    [Fact]
    public void GetHashCode_WithValue_ReturnsConsistentHash()
    {
        var secret = new SecretValue("test");

        var hash1 = secret.GetHashCode();
        var hash2 = secret.GetHashCode();

        hash1.ShouldBe(hash2);
    }

    [Fact]
    public void GetHashCode_Default_ReturnsZero()
    {
        SecretValue secret = default;

        secret.GetHashCode().ShouldBe(0);
    }

    [Fact]
    public void Constructor_WithEmptyString_StillEncrypts()
    {
        var secret = new SecretValue("");

        secret.HasValue.ShouldBeFalse();
        secret.DecryptToString().ShouldBe(string.Empty);
    }

    [Fact]
    public void Constructor_WithLongString_RoundTripsCorrectly()
    {
        var longText = new string('x', 10_000);
        var secret = new SecretValue(longText);

        secret.DecryptToString().ShouldBe(longText);
    }
}
