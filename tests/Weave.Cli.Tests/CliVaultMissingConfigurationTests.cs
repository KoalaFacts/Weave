namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class CliVaultMissingConfigurationTests
{
    [Fact]
    public Task ResolveReference_MissingVaultConfiguration_RejectsBeforeCreatingTransport() =>
        SiloLauncherProcessHarness.RunAsync(typeof(CliVaultMissingConfigurationTests), _ =>
        {
            var previousAddress = Environment.GetEnvironmentVariable("VAULT_ADDR");
            var previousToken = Environment.GetEnvironmentVariable("VAULT_TOKEN");
            try
            {
                Environment.SetEnvironmentVariable("VAULT_ADDR", null);
                Environment.SetEnvironmentVariable("VAULT_TOKEN", null);
                var resolver = new CliSecretResolver();

                var missingAddress = Should.Throw<InvalidOperationException>(() => resolver.ResolveReference("vault:secret/reviews"));
                missingAddress.Message.ShouldContain("VAULT_ADDR environment variable is not set");

                Environment.SetEnvironmentVariable("VAULT_ADDR", "invalid-address-never-used-without-token");
                var missingToken = Should.Throw<InvalidOperationException>(() => resolver.ResolveReference("vault:secret/reviews"));
                missingToken.Message.ShouldContain("VAULT_TOKEN environment variable is not set");
            }
            finally
            {
                Environment.SetEnvironmentVariable("VAULT_ADDR", previousAddress);
                Environment.SetEnvironmentVariable("VAULT_TOKEN", previousToken);
            }
            return Task.CompletedTask;
        });
}
