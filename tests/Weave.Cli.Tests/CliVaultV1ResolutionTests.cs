namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class CliVaultV1ResolutionTests
{
    [Fact]
    public Task ResolveReference_OwnedVaultV1_ReadsValueAndAuthenticatesExactPath() =>
        CliVaultProcessFixture.RunAsync(typeof(CliVaultV1ResolutionTests), "secret/reviews",
            """{"data":{"value":"Host=fixture-v1;Password=fake-only"}}""", 200, resolver =>
            {
                resolver.ResolveReference("VaUlT: secret/reviews ").ShouldBe("Host=fixture-v1;Password=fake-only");
            });
}
