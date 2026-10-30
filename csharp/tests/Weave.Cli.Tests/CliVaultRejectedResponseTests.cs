namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class CliVaultRejectedResponseTests
{
    [Fact]
    public Task ResolveReference_OwnedVaultRejectsRequest_ThrowsInsteadOfReturningResponseBody() =>
        CliVaultProcessFixture.RunAsync(typeof(CliVaultRejectedResponseTests), "secret/reviews",
            """{"errors":["permission denied"]}""", 403, resolver =>
            {
                var exception = Should.Throw<HttpRequestException>(() => resolver.ResolveReference("vault:secret/reviews"));
                exception.Message.ShouldStartWith("Vault returned 403:");
            });
}
