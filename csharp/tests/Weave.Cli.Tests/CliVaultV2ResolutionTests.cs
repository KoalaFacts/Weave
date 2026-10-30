namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class CliVaultV2ResolutionTests
{
    [Fact]
    public Task ResolveReference_OwnedVaultV2_PrefersNestedValueOverTopLevelValue() =>
        CliVaultProcessFixture.RunAsync(typeof(CliVaultV2ResolutionTests), "secret/data/reviews",
            """{"data":{"data":{"value":"Host=fixture-v2;Password=fake-only"},"value":"top-level-decoy","metadata":{"version":2}}}""", 200, resolver =>
            {
                resolver.ResolveReference("vault:secret/data/reviews").ShouldBe("Host=fixture-v2;Password=fake-only");
            });
}
