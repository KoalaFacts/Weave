using System.Text.Json;
using System.Text.RegularExpressions;

namespace Weave.Cli.Tests;

[Collection("CLI init PTY")]
[Trait("Category", "Integration")]
public sealed partial class InitStorageReferenceTerminalTests
{
    [Theory]
    [InlineData("next-init-postgresql", "postgresql", "env:WEAVE_PG_CONNECTION", "env:WEAVE_API_SECRET", 1)]
    [InlineData("next-init-sqlserver", "sqlserver", "inline", "vault:secret/data/fixture/api", 1)]
    [InlineData("next-init-redis", "redis", "file", "env:WEAVE_API_SECRET", 1)]
    [InlineData("next-init-vault-missing", "postgresql", "vault:secret/data/weave/connection", "file", 0)]
    [InlineData("next-init-file-missing", "postgresql", "file", null, 0)]
    public async Task Init_StorageAndSecurityReferences_PersistsChoicesWithoutResolvingAuthOrStartingDatabase(
        string scenario, string backend, string connectionReference, string? authReference, int expectedProbes)
    {
        using var fixture = new InitWizardProcessFixture();

        using var result = await fixture.RunAsync(scenario);

        using var config = fixture.ReadConfig();
        using var input = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Join(fixture.Private, "fixture-input.json"), TestContext.Current.CancellationToken));
        var connection = input.RootElement.GetProperty("connection").GetString();
        var expectedConnection = connectionReference switch
        {
            "inline" => connection.ShouldNotBeNull(),
            "file" => "file:" + Path.Join(fixture.Private, "connection.secret"),
            _ => connectionReference
        };
        config.RootElement.GetProperty("storage").GetString().ShouldBe(backend);
        config.RootElement.GetProperty("connectionString").GetString().ShouldBe(expectedConnection);
        config.RootElement.GetProperty("defaultPort").GetInt32().ShouldBe(9401);
        config.RootElement.TryGetProperty("siloPath", out _).ShouldBeFalse();
        config.RootElement.GetProperty("authMode").GetString().ShouldBe(authReference is null ? "none" : "apikey");
        config.RootElement.GetProperty("requireHttps").GetBoolean().ShouldBe(authReference is not null);
        if (authReference is null)
            config.RootElement.TryGetProperty("authSecret", out _).ShouldBeFalse();
        else
            config.RootElement.GetProperty("authSecret").GetString().ShouldBe(authReference == "file"
                ? "file:" + Path.Join(fixture.Private, "api.secret") : authReference);
        result.RootElement.GetProperty("probeConnections").GetInt32().ShouldBe(expectedProbes);
        var output = result.RootElement.GetProperty("output").GetString().ShouldNotBeNull();
        output.ShouldContain("Environment configured.");
        if (expectedProbes == 1)
            output.ShouldContain("Connection successful.");
        else
        {
            output.ShouldContain("Could not resolve connection string:");
            output.ShouldNotContain("Connection successful.");
        }
        if (backend != "redis")
            output.ShouldContain("Run the Main and Persistence scripts");
        if (connectionReference != "inline")
        {
            config.RootElement.GetRawText().ShouldNotContain("fixture-only-value");
            output.ShouldNotContain("fixture-only-value");
        }
        if (scenario == "next-init-postgresql")
        {
            output.ShouldContain("Environment variable WEAVE_PG_CONNECTION is already set.");
            output.ShouldContain("export WEAVE_API_SECRET=");
            output.ShouldNotContain("WEAVE_API_SECRET is set.");
        }
        if (scenario == "next-init-sqlserver")
            output.ShouldContain("Storing connection strings in config is not recommended");
        if (scenario == "next-init-redis")
        {
            (await File.ReadAllTextAsync(Path.Join(fixture.Private, "connection.secret"),
                TestContext.Current.CancellationToken)).ShouldBe(connection + "\n");
            output.ShouldContain("Generated API key. Set it before starting:");
            GeneratedKeyExport().IsMatch(output).ShouldBeTrue();
            GeneratedKeyValue().IsMatch(config.RootElement.GetRawText()).ShouldBeFalse();
        }
        if (scenario == "next-init-vault-missing")
        {
            output.ShouldContain("export VAULT_ADDR=");
            output.ShouldContain("export VAULT_TOKEN=");
            output.ShouldContain("Create the file with your API secret:");
            File.Exists(Path.Join(fixture.Private, "api.secret")).ShouldBeFalse();
        }
        if (scenario == "next-init-file-missing")
        {
            output.ShouldContain("Create the secret file with your connection string:");
            File.Exists(Path.Join(fixture.Private, "connection.secret")).ShouldBeFalse();
        }
        File.Exists(Path.Join(fixture.Private, "weave.db")).ShouldBeFalse();
    }

    [GeneratedRegex("export WEAVE_API_SECRET=\"[0-9a-f]{64}\"", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex GeneratedKeyExport();

    [GeneratedRegex("[0-9a-f]{64}", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex GeneratedKeyValue();
}
