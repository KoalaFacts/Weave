namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class CliVaultMissingValueTests
{
    [Fact]
    public Task ResolveReference_OwnedVaultWithoutValue_RejectsIncompleteSecret() =>
        CliVaultProcessFixture.RunAsync(typeof(CliVaultMissingValueTests), "secret/reviews",
            """{"data":{"other":"fake-unrelated-field"}}""", 200, resolver =>
            {
                var exception = Should.Throw<KeyNotFoundException>(() => resolver.ResolveReference("vault:secret/reviews"));
                exception.Message.ShouldContain("secret/reviews");
                exception.Message.ShouldContain("no 'value' field");
            });
}
